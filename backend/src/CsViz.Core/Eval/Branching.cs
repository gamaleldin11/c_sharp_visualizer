using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;

namespace CsViz.Core.Eval;

/// A point on the continuation stack that `break` or `continue` can jump to.
///
/// Roslyn gives every loop and switch an ExitLabel, and every loop a ContinueLabel;
/// IBranchOperation.Target names one of them. Matching on the label symbol rather than
/// counting stack entries means a `break` inside nested loops always lands on the right one,
/// including when the inner construct is a switch.
public interface IBranchTarget
{
    ILabelSymbol? Label { get; }
}

/// Sits below a loop or switch body. Reaching it normally means the construct finished; a
/// `break` unwinds to and past it.
public sealed class BreakTargetCont : Continuation, IBranchTarget, IExceptionHandlerContinuation
{
    public ILabelSymbol? Label { get; }
    public BreakTargetCont(ILabelSymbol? label) => Label = label;
    public override void Execute(Evaluator eval) { }
}

/// `break` / `continue`.
public class BranchCont : Continuation
{
    private readonly IBranchOperation _op;
    public BranchCont(IBranchOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        switch (_op.BranchKind)
        {
            case BranchKind.Break:
                eval.UnwindToLabel(_op.Target, inclusive: true);
                break;
            case BranchKind.Continue:
                // Stop *above* the continue target so it runs next and drives the loop on to
                // the increment and the re-test.
                eval.UnwindToLabel(_op.Target, inclusive: false);
                break;
            default:
                throw new NotSupportedException(
                    "goto is not supported. Use a loop or an if/else instead.");
        }
    }
}

/// A `switch` statement.
///
/// C# forbids implicit fall-through between sections, so exactly one section runs and the
/// section ends with break/return/throw. That makes dispatch a simple search for the matching
/// section rather than a jump table with fall-through edges.
public class SwitchDispatchCont : Continuation
{
    private readonly ISwitchOperation _op;
    public SwitchDispatchCont(ISwitchOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Pop();

        ISwitchCaseOperation? matched = null;
        ISwitchCaseOperation? defaultCase = null;

        foreach (var section in _op.Cases)
        {
            foreach (var clause in section.Clauses)
            {
                if (clause is IDefaultCaseClauseOperation)
                {
                    // Held back: `default` only runs when no explicit label matches, and it
                    // is legal to write it first.
                    defaultCase = section;
                    continue;
                }

                if (TryGetClauseConstant(clause, out var constant) && ConstantEquals(value, constant))
                {
                    matched = section;
                    break;
                }
            }
            if (matched != null) break;
        }

        matched ??= defaultCase;

        // Pushed even when nothing matched: an empty switch still has to consume its own
        // break target so the stack stays balanced.
        eval.ContStack.Push(new BreakTargetCont(_op.ExitLabel));

        if (matched == null) return;

        for (int i = matched.Body.Length - 1; i >= 0; i--)
        {
            eval.ContStack.Push(new EvalOperationCont(matched.Body[i]));
        }
    }

    /// Case labels are compile-time constants in C#, so they are read off the operation
    /// rather than evaluated. Both the classic single-value clause and the pattern clause
    /// Roslyn produces for `case 1:` in newer language versions are handled.
    private static bool TryGetClauseConstant(ICaseClauseOperation clause, out object? constant)
    {
        constant = null;
        switch (clause)
        {
            case ISingleValueCaseClauseOperation single:
                if (!single.Value.ConstantValue.HasValue) return false;
                constant = single.Value.ConstantValue.Value;
                return true;

            case IPatternCaseClauseOperation pattern when pattern.Guard == null:
                if (pattern.Pattern is IConstantPatternOperation cp && cp.Value.ConstantValue.HasValue)
                {
                    constant = cp.Value.ConstantValue.Value;
                    return true;
                }
                return false;

            default:
                return false;
        }
    }

    private static bool ConstantEquals(IValue value, object? constant)
    {
        if (constant == null) return value is NullValue;
        if (value is not PrimitiveValue pv) return false;

        // Compare numerically rather than by boxed identity: the case label may be an int
        // while the switch value is a long, and `1L == 1` must still match.
        try
        {
            if (pv.Value is string s) return constant is string cs && string.Equals(s, cs, StringComparison.Ordinal);
            if (pv.Value is bool b) return constant is bool cb && b == cb;
            if (pv.Value is char || constant is char || IsNumber(pv.Value))
            {
                return Convert.ToDecimal(pv.Value is char pc ? (int)pc : pv.Value,
                           System.Globalization.CultureInfo.InvariantCulture)
                     == Convert.ToDecimal(constant is char cc ? (int)cc : constant,
                           System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (InvalidCastException) { return false; }
        catch (FormatException) { return false; }
        catch (OverflowException) { return false; }

        return Equals(pv.Value, constant);
    }

    private static bool IsNumber(object v) =>
        v is int or long or short or byte or uint or ulong or ushort or sbyte or float or double or decimal;
}
