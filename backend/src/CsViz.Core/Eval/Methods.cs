using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;
using CsViz.Core.Frames;
using CsViz.Core.Heap;

namespace CsViz.Core.Eval;

public class InvocationOpCont : Continuation
{
    private readonly IInvocationOperation _op;
    public InvocationOpCont(IInvocationOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var args = new IValue[_op.Arguments.Length];
        for (int i = _op.Arguments.Length - 1; i >= 0; i--)
        {
            args[i] = eval.ValueStack.Pop();
        }

        IValue instance = NullValue.Instance;
        if (_op.Instance != null)
        {
            instance = eval.ValueStack.Pop();
        }

        if (_op.Instance != null)
        {
            eval.ValueStack.Push(instance);
        }
        for (int i = 0; i < args.Length; i++)
        {
            eval.ValueStack.Push(args[i]);
        }

        // ref / out arguments were pushed as lvalue references by the visitor, in argument
        // order, so they pop in reverse. Each is paired with the parameter it feeds, and
        // PopFrameCont writes the callee's final value back into it.
        List<(int Index, IReference Target)>? refBindings = null;
        for (int i = _op.Arguments.Length - 1; i >= 0; i--)
        {
            if (Evaluator.IsByReference(_op.Arguments[i]))
            {
                (refBindings ??= new()).Add((i, eval.RefStack.Pop()));
            }
        }

        eval.ContStack.Push(new MethodCallCont(
            _op.TargetMethod, _op.Instance != null, args.Length, _op.IsVirtual, refBindings));
    }
}

public class MethodCallCont : Continuation
{
    private readonly IMethodSymbol _method;
    private readonly bool _hasInstance;
    private readonly int _argCount;
    private readonly bool _isVirtual;
    private readonly IReadOnlyList<(int Index, IReference Target)>? _refBindings;

    public MethodCallCont(
        IMethodSymbol method,
        bool hasInstance,
        int argCount,
        bool isVirtual = false,
        IReadOnlyList<(int Index, IReference Target)>? refBindings = null)
    {
        _method = method;
        _hasInstance = hasInstance;
        _argCount = argCount;
        _isVirtual = isVirtual;
        _refBindings = refBindings;
    }

    public override void Execute(Evaluator eval)
    {
        var args = new IValue[_argCount];
        for (int i = _argCount - 1; i >= 0; i--)
        {
            args[i] = eval.ValueStack.Pop();
        }
        
        IValue instance = NullValue.Instance;
        if (_hasInstance)
        {
            instance = eval.ValueStack.Pop();
        }
        
        var methodToCall = _method;

        // Every call leaves exactly one value on the stack, constructors included (they leave
        // null). The old asymmetry - constructors pushing nothing - meant any caller that
        // discarded a result, such as the expression statement Roslyn wraps `: base(...)` in,
        // popped a value belonging to somebody else.
        if (CsViz.Core.Bridge.BCLBridge.TryInvoke(methodToCall, instance, args, eval, out var result))
        {
            eval.ValueStack.Push(result);
            return;
        }

        // Virtual dispatch
        if (_isVirtual && instance is ObjectRef objRef)
        {
            if (eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj is ClassObject clsObj)
            {
                var runtimeType = eval.GetTypeSymbol(clsObj.TypeId);
                if (methodToCall.ContainingType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface)
                {
                    var impl = runtimeType.FindImplementationForInterfaceMember(methodToCall) as IMethodSymbol;
                    if (impl != null) methodToCall = impl;
                }
                else
                {
                    var overrideMethod = FindOverride(methodToCall, runtimeType);
                    if (overrideMethod != null) methodToCall = overrideMethod;
                }
            }
        }

        bool isConstructor = methodToCall.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor;

        var body = eval.MethodProvider.GetMethodBody(methodToCall);
        if (body == null)
        {
            // A compiler-generated constructor, or one that came from metadata, has no body to
            // interpret. The type's field initializers still have to run: without this, a
            // class with `public int Value = 7;` and no explicit constructor reported 0.
            if (isConstructor)
            {
                // Same rule as PopFrameCont: a struct constructor yields the struct.
                eval.ValueStack.Push(methodToCall.ContainingType.IsValueType ? instance : NullValue.Instance);
                ScheduleFieldInitializers(eval, methodToCall, instance);
                return;
            }

            throw new NotSupportedException(
                $"'{methodToCall.ContainingType.Name}.{methodToCall.Name}' is not a supported method. " +
                "Only the documented subset of the .NET library is available.");
        }

        var frame = new Frame
        {
            Id = eval.FrameStack.Count + 1,
            Method = methodToCall,
            ThisValue = instance
        };
        
        for (int i = 0; i < methodToCall.Parameters.Length; i++)
        {
            var p = methodToCall.Parameters[i];
            var slot = new Slot
            {
                SlotId = i + 1,
                Name = p.Name,
                Kind = SlotKind.Param,
                InScope = true,
                // An `out` parameter starts unassigned, exactly as C# says it does.
                Value = i < args.Length ? args[i] : UnsetValue.Instance
            };
            frame.Slots.Add(slot);

            // Keyed by the original definition. For a constructed generic method the symbols in
            // methodToCall.Parameters are substituted (T becomes int) while the ones inside the
            // body are the definition's, and SymbolEqualityComparer treats those as different -
            // so every parameter read in a generic method resolved to Unset and the method
            // silently returned nothing.
            frame.SlotMap[p.OriginalDefinition] = slot.SlotId;
        }

        // ref / out: the caller's storage location, to copy the final value back into.
        frame.RefBindings = _refBindings;

        // Pre-declare every local the method body can introduce, before the frame is pushed.
        // Slots are otherwise created lazily on block entry, and the "scope" delta op carries
        // only an id - so a frontend replaying deltas would have no name for any local
        // declared after the frame was pushed, and would render "slot2" instead of "total".
        DeclareLocals(body, frame);

        eval.PushFrame(frame);
        eval.ContStack.Push(new PopFrameCont());
        eval.ContStack.Push(new EvalOperationCont(body));

        // C# constructor order: this type's field initializers, then the base constructor,
        // then this constructor's body. Pushed in reverse, so the body above runs first and
        // the field initializers below run last.
        if (isConstructor)
        {
            var initializer = eval.MethodProvider.GetConstructorInitializer(methodToCall);
            if (initializer != null)
            {
                // Roslyn may hand back either the invocation itself or an expression statement
                // wrapping it. The wrapper discards its own result; a bare invocation does not,
                // so the call's null return has to be dropped here instead.
                if (initializer is not IExpressionStatementOperation)
                {
                    eval.ContStack.Push(new DiscardResultCont());
                }
                eval.ContStack.Push(new EvalOperationCont(initializer));
            }

            // A `: this(...)` chain runs the other constructor, which runs the initializers
            // itself. Running them here too would apply every initializer twice.
            bool chainsToSameType = FindInvocation(initializer) is { } chain &&
                SymbolEqualityComparer.Default.Equals(chain.TargetMethod.ContainingType, methodToCall.ContainingType);

            if (!chainsToSameType)
            {
                ScheduleFieldInitializers(eval, methodToCall, instance);
            }
        }
    }

    private static IInvocationOperation? FindInvocation(IOperation? op) => op switch
    {
        IInvocationOperation invocation => invocation,
        IExpressionStatementOperation { Operation: IInvocationOperation inner } => inner,
        _ => null
    };

    /// Queues the type's `= value` field initializers to run against `instance`.
    ///
    /// The instance is passed explicitly rather than read from the frame, because a
    /// compiler-generated constructor has no frame at all. C# forbids a field initializer from
    /// referencing an instance member, so nothing in the value expression needs `this`.
    private static void ScheduleFieldInitializers(Evaluator eval, IMethodSymbol constructor, IValue instance)
    {
        var initializers = eval.MethodProvider.GetFieldInitializers(constructor.ContainingType);
        for (int i = initializers.Count - 1; i >= 0; i--)
        {
            eval.ContStack.Push(new FieldInitializerCont(initializers[i], instance));
            eval.ContStack.Push(new EvalOperationCont(initializers[i].Value));
        }
    }


    /// Walks the body collecting locals from every scope-introducing operation, adding a
    /// declared-but-out-of-scope slot for each. EnterBlockScopeCont then flips InScope.
    public static void DeclareLocals(IOperation op, Frame frame)
    {
        System.Collections.Immutable.ImmutableArray<ILocalSymbol> locals = op switch
        {
            IBlockOperation b => b.Locals,
            IForLoopOperation f => f.Locals,
            IForEachLoopOperation fe => fe.Locals,
            ICatchClauseOperation c => c.Locals,
            ISwitchOperation sw => sw.Locals,
            ISwitchCaseOperation sc => sc.Locals,
            IUsingOperation u => u.Locals,
            _ => System.Collections.Immutable.ImmutableArray<ILocalSymbol>.Empty
        };

        foreach (var local in locals)
        {
            if (frame.SlotMap.ContainsKey(local)) continue;
            int slotId = frame.Slots.Count + 1;
            frame.SlotMap[local] = slotId;
            frame.Slots.Add(new Slot
            {
                SlotId = slotId,
                Name = local.Name,
                Kind = SlotKind.Local,
                DeclaredLine = local.Locations.Length > 0 && !local.Locations[0].IsInMetadata
                    ? local.Locations[0].GetLineSpan().StartLinePosition.Line + 1
                    : 0,
                InScope = false,
                Value = UnsetValue.Instance
            });
        }

        foreach (var child in op.ChildOperations)
        {
            DeclareLocals(child, frame);
        }
    }

    private IMethodSymbol? FindOverride(IMethodSymbol baseMethod, ITypeSymbol runtimeType)
    {
        var current = runtimeType;
        while (current != null && !SymbolEqualityComparer.Default.Equals(current, baseMethod.ContainingType))
        {
            var members = current.GetMembers(baseMethod.Name).OfType<IMethodSymbol>();
            foreach (var m in members)
            {
                if (m.IsOverride)
                {
                    var overridden = m.OverriddenMethod;
                    while (overridden != null)
                    {
                        if (SymbolEqualityComparer.Default.Equals(overridden, baseMethod))
                        {
                            return m;
                        }
                        overridden = overridden.OverriddenMethod;
                    }
                }
            }
            current = current.BaseType;
        }
        return null;
    }
}

/// Writes the value of a `public int V = 5;` style initializer onto the object being built.
public class FieldInitializerCont : Continuation
{
    private readonly IFieldInitializerOperation _op;
    private readonly IValue _instance;

    public FieldInitializerCont(IFieldInitializerOperation op, IValue instance)
    {
        _op = op;
        _instance = instance;
    }

    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Pop();
        foreach (var field in _op.InitializedFields)
        {
            // `int a = b = 0;` declares several fields from one value expression, so each is
            // written from the same evaluated result.
            if (field.IsStatic)
            {
                eval.WriteReference(eval.StaticFieldRef(field), value);
            }
            else if (_instance is ObjectRef objRef)
            {
                eval.WriteReference(new FieldRef(objRef.ObjId, field.Name), value);
            }
        }
    }
}

public class PopFrameCont : Continuation, IExceptionHandlerContinuation
{
    public override void Execute(Evaluator eval)
    {
        var frame = eval.CurrentFrame;
        var method = frame.Method;
        var thisValue = frame.ThisValue;

        // ref / out are modelled as copy-in, copy-out: the callee worked on its own slot, and
        // its final value is written back into the caller's variable now. That is
        // indistinguishable from true aliasing for single-threaded code, and it keeps
        // parameters as ordinary slots the memory view already knows how to draw.
        //
        // Skipped while an exception is unwinding, because C# does not write back either.
        if (eval.UnwindingException == null && frame.RefBindings is { } bindings)
        {
            foreach (var (index, target) in bindings)
            {
                var slot = frame.Slots.Find(s => s.SlotId == index + 1);
                if (slot != null && slot.Value is not UnsetValue)
                {
                    eval.WriteReference(target, slot.Value);
                }
            }
        }

        eval.PopFrame();
        
        // Constructors included: every call leaves exactly one value behind, so that callers
        // which discard a result can do so unconditionally.
        if (eval.UnwindingException == null &&
            (method.ReturnsVoid || method.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor))
        {
            // A struct constructor yields the struct it just built. Its writes went to the
            // frame's `this`, which is a fresh value after each field assignment, so the
            // finished struct only exists here.
            bool buildsStruct = method.MethodKind == Microsoft.CodeAnalysis.MethodKind.Constructor &&
                                method.ContainingType.IsValueType;
            eval.ValueStack.Push(buildsStruct ? thisValue : NullValue.Instance);
        }
    }
}

/// `return`. The returned value, if any, is already on the value stack.
public class ReturnCont : Continuation
{
    public override void Execute(Evaluator eval)
    {
        while (eval.ContStack.Count > 0)
        {
            var cont = eval.ContStack.Pop();
            if (cont is PopFrameCont)
            {
                // Re-push it so the frame is popped through the normal path.
                eval.ContStack.Push(cont);
                return;
            }

            // Bookkeeping continuations still have to run on the way out. Discarding them
            // silently was costing the trace most of its steps: an early `return` threw away
            // every enclosing statement's EndStepCont, so a recursive Fib(6) - 26 calls -
            // recorded only 14 steps, and the encoder was left with steps it never closed.
            if (cont is EndStepCont or ExitBlockScopeCont or ExitForEachScopeCont or FinallyCont)
            {
                cont.Execute(eval);
            }
        }
    }
}
