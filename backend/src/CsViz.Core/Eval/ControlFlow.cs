using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;
using CsViz.Core.Heap;
using CsViz.Core.Frames;

namespace CsViz.Core.Eval;

public class ConditionalCont : Continuation
{
    private readonly IConditionalOperation _cond;
    public ConditionalCont(IConditionalOperation cond) => _cond = cond;

    public override void Execute(Evaluator eval)
    {
        var conditionValue = eval.ValueStack.Pop();
        if (conditionValue is PrimitiveValue { Value: bool b })
        {
            var branch = b ? _cond.WhenTrue : _cond.WhenFalse;
            if (branch != null)
            {
                eval.ContStack.Push(new EvalOperationCont(branch));
            }
            else if (_cond.Type != null)
            {
                // A ternary always yields a value; a missing branch here would leave the
                // value stack short and desynchronise every later pop.
                eval.ValueStack.Push(NullValue.Instance);
            }
        }
        else
        {
            throw new Exception("Condition is not a boolean");
        }
    }
}

// ---------------------------------------------------------------------------------------
// Loops
//
// Every loop lays the continuation stack out the same way, bottom to top:
//
//     BreakTargetCont(ExitLabel)     <- `break` unwinds past this; falling into it ends the loop
//     LoopIterCont(loop)             <- `continue` unwinds to this; runs increments, then re-tests
//     ...body continuations...
//
// Because both jump destinations are real stack entries tagged with Roslyn's own label
// symbols, break and continue in nested loops need no depth counting: the label decides.
// ---------------------------------------------------------------------------------------

/// Starts a loop: installs the break target, then either tests first (while/for) or runs the
/// body first (do-while).
public class LoopStartCont : Continuation
{
    private readonly ILoopOperation _loop;
    public LoopStartCont(ILoopOperation loop) => _loop = loop;

    public override void Execute(Evaluator eval)
    {
        eval.ContStack.Push(new BreakTargetCont(_loop.ExitLabel));

        bool conditionIsTop = _loop is not IWhileLoopOperation w || w.ConditionIsTop;
        var condition = ConditionOf(_loop);

        if (!conditionIsTop)
        {
            // do { } while (...): the body runs before the first test, and `continue` inside
            // it goes to that test, which is exactly where LoopIterCont sits.
            eval.ContStack.Push(new LoopIterCont(_loop));
            eval.ContStack.Push(new EvalOperationCont(BodyOf(_loop)));
            return;
        }

        if (condition == null)
        {
            // `for(;;)`. Unconditional; the step budget is what terminates it.
            eval.ContStack.Push(new LoopIterCont(_loop));
            eval.ContStack.Push(new EvalOperationCont(BodyOf(_loop)));
            return;
        }

        eval.ContStack.Push(new LoopTestCont(_loop));
        eval.ContStack.Push(new EvalOperationCont(condition));
    }

    internal static IOperation? ConditionOf(ILoopOperation loop) => loop switch
    {
        IWhileLoopOperation w => w.Condition,
        IForLoopOperation f => f.Condition,
        _ => null
    };

    internal static IOperation BodyOf(ILoopOperation loop) => loop.Body!;
}

/// The loop's continue target. Runs the increment clauses, then re-evaluates the condition.
public class LoopIterCont : Continuation, IBranchTarget
{
    private readonly ILoopOperation _loop;
    public LoopIterCont(ILoopOperation loop) => _loop = loop;
    public ILabelSymbol? Label => _loop.ContinueLabel;

    public override void Execute(Evaluator eval)
    {
        var condition = LoopStartCont.ConditionOf(_loop);

        if (condition == null)
        {
            eval.ContStack.Push(new LoopIterCont(_loop));
            eval.ContStack.Push(new EvalOperationCont(LoopStartCont.BodyOf(_loop)));
        }
        else
        {
            eval.ContStack.Push(new LoopTestCont(_loop));
            eval.ContStack.Push(new EvalOperationCont(condition));
        }

        // Pushed last so they pop first: the increments run before the condition is re-read.
        // Reverse order within the clause list keeps `i++, j--` evaluating left to right.
        if (_loop is IForLoopOperation forLoop)
        {
            for (int i = forLoop.AtLoopBottom.Length - 1; i >= 0; i--)
            {
                eval.ContStack.Push(new EvalOperationCont(forLoop.AtLoopBottom[i]));
            }
        }
    }
}

/// Consumes the condition value and either runs another iteration or lets the stack fall
/// through to the loop's BreakTargetCont.
public class LoopTestCont : Continuation
{
    private readonly ILoopOperation _loop;
    public LoopTestCont(ILoopOperation loop) => _loop = loop;

    public override void Execute(Evaluator eval)
    {
        var conditionValue = eval.ValueStack.Pop();
        if (conditionValue is not PrimitiveValue { Value: bool b })
        {
            throw new Exception("Loop condition is not a boolean");
        }

        if (!b) return;

        eval.ContStack.Push(new LoopIterCont(_loop));
        eval.ContStack.Push(new EvalOperationCont(LoopStartCont.BodyOf(_loop)));
    }
}

/// Materialises the collection a `foreach` runs over.
///
/// The sequence is snapshotted rather than enumerated lazily. That is a real, documented
/// limitation - a collection mutated during iteration will not throw the way real C# does -
/// but it keeps the loop free of interpreter-visible iterator state, which is what a lazy
/// enumerator would require.
public class ForEachStartCont : Continuation
{
    private readonly IForEachLoopOperation _forEach;
    public ForEachStartCont(IForEachLoopOperation forEach) => _forEach = forEach;

    public override void Execute(Evaluator eval)
    {
        var collection = eval.ValueStack.Pop();
        IValue[] items;

        if (collection is NullValue)
        {
            eval.UnwindingException = new BuiltinExceptionValue("NullReferenceException");
            return;
        }

        if (collection is PrimitiveValue { Value: string str })
        {
            items = new IValue[str.Length];
            for (int i = 0; i < str.Length; i++) items[i] = new PrimitiveValue(TypeCode.Char, str[i]);
        }
        else if (collection is ObjectRef oRef && eval.Heap.TryGet(oRef.ObjId, out var heapObj))
        {
            items = heapObj switch
            {
                ArrayObject arr => arr.Elems,
                ListObject list => list.Backing[..list.Count],
                StackObject st => Reversed(st.Items),
                QueueObject q => q.Items.ToArray(),
                DictObject dict => DictItems(dict, eval),
                _ => throw new NotSupportedException($"Cannot enumerate {heapObj.GetType().Name}.")
            };
        }
        else
        {
            throw new NotSupportedException($"Cannot enumerate {collection.GetType().Name}.");
        }

        eval.ContStack.Push(new BreakTargetCont(_forEach.ExitLabel));
        eval.ContStack.Push(new ForEachIterCont(_forEach, items, 0));
    }

    /// Stack<T> enumerates top-first, the reverse of push order.
    private static IValue[] Reversed(List<IValue> items)
    {
        var copy = items.ToArray();
        Array.Reverse(copy);
        return copy;
    }

    private static IValue[] DictItems(DictObject dict, Evaluator eval)
    {
        var items = new IValue[dict.Entries.Count];
        for (int i = 0; i < dict.Entries.Count; i++)
        {
            var kvp = dict.Entries[i];
            var fields = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, IValue>();
            fields["Key"] = kvp.Key;
            fields["Value"] = kvp.Value;
            items[i] = new StructValue(eval.KeyValuePairType(dict), fields.ToImmutable());
        }
        return items;
    }
}

/// One iteration of a `foreach`, and the loop's continue target.
public class ForEachIterCont : Continuation, IBranchTarget
{
    private readonly IForEachLoopOperation _forEach;
    private readonly IValue[] _items;
    private readonly int _index;

    public ForEachIterCont(IForEachLoopOperation forEach, IValue[] items, int index)
    {
        _forEach = forEach;
        _items = items;
        _index = index;
    }

    public ILabelSymbol? Label => _forEach.ContinueLabel;

    public override void Execute(Evaluator eval)
    {
        if (_index >= _items.Length) return;

        var local = LoopControlLocal(_forEach);
        if (local != null && eval.CurrentFrame.SlotMap.TryGetValue(local, out var slotId))
        {
            // Written through WriteReference rather than straight onto the slot so the loop
            // variable's change appears in the trace; assigning the field directly is why an
            // earlier version showed the loop variable frozen at its first value.
            eval.WriteReference(new LocalRef(eval.CurrentFrame.Id, slotId), _items[_index]);
        }

        eval.ContStack.Push(new ForEachIterCont(_forEach, _items, _index + 1));
        eval.ContStack.Push(new EvalOperationCont(_forEach.Body));
    }

    internal static ILocalSymbol? LoopControlLocal(IForEachLoopOperation forEach) =>
        forEach.LoopControlVariable switch
        {
            IVariableDeclaratorOperation v => v.Symbol,
            IVariableDeclarationGroupOperation g when g.Declarations is [{ Declarators: [var d, ..] }, ..] => d.Symbol,
            IVariableDeclarationOperation d when d.Declarators is [var first, ..] => first.Symbol,
            IDeclarationExpressionOperation { Expression: ILocalReferenceOperation loc } => loc.Local,
            ILocalReferenceOperation loc => loc.Local,
            _ => null
        };
}
