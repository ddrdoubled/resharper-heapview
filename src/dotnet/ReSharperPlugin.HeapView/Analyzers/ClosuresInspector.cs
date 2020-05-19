﻿using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using JetBrains.Diagnostics;
using JetBrains.ReSharper.Psi;
using JetBrains.ReSharper.Psi.CSharp.DeclaredElements;
using JetBrains.ReSharper.Psi.CSharp.Tree;
using JetBrains.ReSharper.Psi.CSharp.Tree.Query;
using JetBrains.ReSharper.Psi.CSharp.Util;
using JetBrains.ReSharper.Psi.ExtensionsAPI.Resolve;
using JetBrains.ReSharper.Psi.Tree;
using JetBrains.Util;

namespace ReSharperPlugin.HeapView.Analyzers
{
  // todo: check ctor initializers
  // todo: https://sharplab.io/#v2:EYLgtghgzgLgpgJwD4AEBMBGAsAKFygZgAJ0iBhIgb1yNpOJQBYiBZACgEoqa7eBLAHYwiADyIBeIgAYA3D1615Cpb2o4FGonwBmRNmPGTGaDis2VeZzXTEBqW3PXXNAcwD2MN0QA2EYHG9HZzoAXytlJ2Dwy0jnX39vEGi6NWCFQWEATwlpILS6FABWAB4MgD4ibQBXAQBjHM4JCrsiTLz8ogB6TvdPHz8A9ucw2N4R63ClEZCgA===

  public sealed class ClosuresInspector : IRecursiveElementProcessor
  {
    [NotNull] private readonly ICSharpDeclaration myDeclaration;
    [CanBeNull] private readonly IParametersOwner myTopLevelParametersOwner;
    [NotNull] private readonly Stack<ICSharpClosure> myCurrentClosures;
    private int myDisplayClassCounter;

    public ClosuresInspector([NotNull] ICSharpDeclaration declaration, [CanBeNull] IParametersOwner topLevelParameterOwner)
    {
      myDeclaration = declaration;
      myTopLevelParametersOwner = topLevelParameterOwner;
      myCurrentClosures = new Stack<ICSharpClosure>();

      Captures = new OneToSetMap<ICSharpClosure, IDeclaredElement>();
      CapturesOfScope = new OneToSetMap<ILocalScope, IDeclaredElement>();
      ClosuresOfScope = new OneToSetMap<ILocalScope, ICSharpClosure>();
      DisplayClasses = new Dictionary<IScope, DisplayClassInfo>();

      AnonymousTypes = new HashSet<IQueryRangeVariableDeclaration>();
      CapturelessClosures = new List<ICSharpClosure>();
      DelayedUseLocalFunctions = new OneToListMap<ILocalFunction, IReferenceExpression>();
    }

    [CanBeNull, Pure]
    public static ClosuresInspector TryBuild([NotNull] ICSharpDeclaration declaration)
    {
      switch (declaration)
      {
        case ICSharpClosure _:
          return null; // only top-level declarations

        // block-bodied methods, constructors, accessors, operators, finalizer
        case ICSharpFunctionDeclaration { Body: { }, DeclaredElement: var parametersOwner }:
          return new ClosuresInspector(declaration, parametersOwner);

        // expression-bodied accessors + get-only properties/indexers
        case IExpressionBodyOwnerDeclaration { ArrowClause: { }, DeclaredElement: IParametersOwner parametersOwner }:
          return new ClosuresInspector(declaration, parametersOwner);

        // field/event/auto-property initializer
        case IFieldDeclaration { Initial: { } }:
        case IEventDeclaration { Initial: { } }:
        case IPropertyDeclaration { Initial: { } }:
          return new ClosuresInspector(declaration, null);

        default:
          return null;
      }
    }

    public void Run()
    {
      myDeclaration.ProcessDescendants(this);
    }

    [CanBeNull] public IParametersOwner TopLevelParametersOwner => myTopLevelParametersOwner;
    [NotNull] public Dictionary<IScope, DisplayClassInfo> DisplayClasses { get; }
    [NotNull] public List<ICSharpClosure> CapturelessClosures { get; }

    // todo: remove?
    [NotNull] public OneToSetMap<ICSharpClosure, IDeclaredElement> Captures { get; }

    [NotNull]
    private DisplayClassInfo GetOrCreateDisplayClassForCapture([NotNull] IDeclaredElement capture)
    {
      var captureScope = GetScopeForCapture(capture);

      if (!DisplayClasses.TryGetValue(captureScope, out var displayClass))
      {
        displayClass = new DisplayClassInfo(myDisplayClassCounter++);
        DisplayClasses.Add(captureScope, displayClass);
      }

      displayClass.AddCapture(capture);

      return displayClass;
    }

    [NotNull, Pure]
    private IScope GetScopeForCapture([NotNull] IDeclaredElement capture)
    {
      if (capture is IParameter { IsValueVariable: true } valueParameter)
      {
        capture = (IAccessor) valueParameter.ContainingParametersOwner.NotNull();
      }

      var firstDeclaration = capture.GetFirstDeclaration<ICSharpDeclaration>().NotNull();
      var containingScope = firstDeclaration.GetContainingScope(returnThis: true);
      if (containingScope == null)
      {
        GC.KeepAlive(firstDeclaration);
      }

      return containingScope.NotNull();
    }

    [Obsolete] [NotNull] public OneToSetMap<ILocalScope, IDeclaredElement> CapturesOfScope { get; }
    [Obsolete] [NotNull] public OneToSetMap<ILocalScope, ICSharpClosure> ClosuresOfScope { get; }

    [NotNull] public HashSet<IQueryRangeVariableDeclaration> AnonymousTypes { get; }
    [NotNull] public OneToListMap<ILocalFunction, IReferenceExpression> DelayedUseLocalFunctions { get; }

    public sealed class DisplayClassInfo
    {
      public DisplayClassInfo(int index)
      {
        Index = index;
      }

      public int Index { get; }
      public HashSet<IDeclaredElement> Captures { get; } = new HashSet<IDeclaredElement>();
      public HashSet<ICSharpClosure> Closures { get; } = new HashSet<ICSharpClosure>();
      [CanBeNull] public DisplayClassInfo ParentDisplayClass { get; private set; }

      public TreeTextRange FirstCapturedVariableLocation { get; private set; }


      public void AddCapture([NotNull] IDeclaredElement capture)
      {
        Captures.Add(capture);

        // update 'FirstCapturedVariableLocation'
      }
    }

    public bool ProcessingIsFinished => false;
    public bool InteriorShouldBeProcessed(ITreeNode element) => true;

    public void ProcessBeforeInterior(ITreeNode element)
    {
      if (element is ICSharpClosure closure)
      {
        myCurrentClosures.Push(closure);
      }

      if (element is ICSharpExpression expression)
      {
        ProcessExpression(expression);
      }
    }

    public void ProcessAfterInterior(ITreeNode element)
    {
      if (element is ICSharpClosure closure)
      {
        ProcessClosureAfterInterior(closure);
      }
    }

    private void ProcessClosureAfterInterior([NotNull] ICSharpClosure closure)
    {
      var lastClosure = myCurrentClosures.Pop();
      Assertion.Assert(lastClosure == closure, "lastClosure == closure");

      if (!Captures.ContainsKey(closure))
      {
        CapturelessClosures.Add(closure);
      }
    }

    private void ProcessExpression(ICSharpExpression expression)
    {
      switch (expression)
      {
        case IThisExpression _:
        case IBaseExpression _:
          AddThisCapture();
          break;

        case IReferenceExpression { QualifierExpression: null } referenceExpression:
          ProcessNotQualifiedReferenceExpression(referenceExpression);
          break;
      }
    }

    private void ProcessNotQualifiedReferenceExpression([NotNull] IReferenceExpression referenceExpression)
    {
      if (referenceExpression.IsNameofOperatorArgumentPart()) return;

      var (declaredElement, _) = referenceExpression.Reference.Resolve();

      if (declaredElement is ILocalFunction function)
      {
        ProcessLocalFunctionUsage(function, referenceExpression);
      }

      if (myCurrentClosures.Count > 0 && declaredElement != null)
      {
        ProcessElementUsedByNonQualifiedReferenceExpressionInClosure(declaredElement);
      }
    }

    private void AddThisCapture()
    {
      if (myCurrentClosures.Count == 0) return;

      var parametersOwner = myTopLevelParametersOwner;
      if (parametersOwner == null) return;

      if (parametersOwner is ITypeMember { IsStatic: true }) return;

      // todo: display class?

      foreach (var closure in myCurrentClosures)
      {
        Captures.Add(closure, parametersOwner);
      }
    }

    private void ProcessElementUsedByNonQualifiedReferenceExpressionInClosure([NotNull] IDeclaredElement declaredElement)
    {
      switch (declaredElement)
      {
        case IParameter parameter:
          AddParameterCapture(parameter);
          return;

        case ILocalVariable localVariable:
          AddLocalVariableCapture(localVariable);
          break;

        case ILocalFunction localFunction:
          AddLocalFunctionCapture(localFunction);
          break;

        case ITypeMember typeMember:
          AddThisCaptureViaMemberUsage(typeMember);
          break;

        case IQueryAnonymousTypeProperty anonymousTypeProperty:
          ProcessAnonymousProperty(anonymousTypeProperty);
          break;

        // note: ITypeParameter capture do not introduces allocations in Roslyn-generated code
      }
    }

    private void AddParameterCapture([NotNull] IParameter parameter)
    {
      var parameterOwner = parameter.ContainingParametersOwner;
      if (parameterOwner == null) return; // should not happen anyway

      GetOrCreateDisplayClassForCapture(parameter);

      foreach (var closure in myCurrentClosures)
      {
        Captures.Add(closure, parameter);

        if (ReferenceEquals(parameterOwner, closure)) break;
      }
    }

    private void AddLocalVariableCapture([NotNull] ILocalVariable localVariable)
    {
      if (localVariable.IsConstant) return;

      GetOrCreateDisplayClassForCapture(localVariable);

      var variableDeclaration = localVariable.GetSingleDeclaration<ICSharpDeclaration>().NotNull();
      //var variableScope = variableDeclaration.GetContainingScope<ILocalScope>(returnThis: true).NotNull();

      foreach (var closure in myCurrentClosures)
      {
        if (closure.Contains(variableDeclaration)
            && !(closure is IQueryParameterPlatform) // todo: query inside query?
            ) break;

        Captures.Add(closure, localVariable);
      }
    }

    private void AddLocalFunctionCapture([NotNull] ILocalFunction localFunction)
    {
      var localFunctionDeclaration = localFunction.GetSingleDeclaration<ILocalFunctionDeclaration>().NotNull();
      //var functionScope = localFunctionDeclaration.GetContainingScope<ILocalScope>(returnThis: true).NotNull();

      foreach (var closure in myCurrentClosures)
      {
        if (closure.Contains(localFunctionDeclaration)) break;

        Captures.Add(closure, localFunction);
      }
    }

    private void AddThisCaptureViaMemberUsage([NotNull] ITypeMember typeMember)
    {
      if (typeMember is ITypeElement) return;
      if (typeMember.IsStatic) return;

      if (typeMember is IField { IsField: false }) return;

      // todo: test with indexer's parameters (on expr-bodied indexers + accessors)
      // todo: test on setter's value parameter

      AddThisCapture();
    }

    private void ProcessLocalFunctionUsage([NotNull] ILocalFunction localFunction, [NotNull] IReferenceExpression referenceExpression)
    {
      var containingExpression = referenceExpression.GetContainingParenthesizedExpression();
      var invocationExpression = InvocationExpressionNavigator.GetByInvokedExpression(containingExpression);
      if (invocationExpression == null)
      {
        // note: nameof(LocalFunc) already filtered here

        DelayedUseLocalFunctions.Add(localFunction, referenceExpression);
      }
    }

    // note: invoked only inside closures
    private void ProcessAnonymousProperty([NotNull] IQueryAnonymousTypeProperty anonymousProperty)
    {
      foreach (var anonymousTypeProperty in anonymousProperty.ContainingType.Properties)
      {
        var property = (IQueryAnonymousTypeProperty) anonymousTypeProperty;
        var declaration = property.Declaration;

        if (QueryFirstFromNavigator.GetByDeclaration(declaration) == null
            && QueryContinuationNavigator.GetByDeclaration(declaration) == null)
        {
          AnonymousTypes.Add(declaration);
        }
      }
    }

    public bool IsDisplayClassForScopeCanBeLoweredToStruct([NotNull] ILocalScope scope)
    {
      foreach (var closure in ClosuresOfScope[scope])
      {
        if (closure is ILocalFunctionDeclaration localFunctionDeclaration)
        {
          if (DelayedUseLocalFunctions.ContainsKey(localFunctionDeclaration.DeclaredElement))
          {
            return false;
          }
        }
        else // lambdas, query - all delayed
        {
          return false;
        }
      }

      return true;
    }
  }
}