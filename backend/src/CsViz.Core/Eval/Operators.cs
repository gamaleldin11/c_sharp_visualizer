using System;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;
using CsViz.Core.Heap;

namespace CsViz.Core.Eval;

/// `-x`, `!b`, `~x`, `+x`.
public class UnaryCont : Continuation
{
    private readonly IUnaryOperation _op;
    public UnaryCont(IUnaryOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var operand = eval.ValueStack.Pop();

        if (operand is not PrimitiveValue pv)
        {
            throw new NotSupportedException($"Unary {_op.OperatorKind} is not supported on {operand.GetType().Name}.");
        }

        switch (_op.OperatorKind)
        {
            case UnaryOperatorKind.Plus:
                eval.ValueStack.Push(pv);
                return;

            case UnaryOperatorKind.Not:
                // On an integer operand this is C#'s bitwise complement; `!` on bool is the
                // same OperatorKind in the IOperation model, distinguished only by the type.
                if (pv.Value is bool b)
                {
                    eval.ValueStack.Push(new PrimitiveValue(TypeCode.Boolean, !b));
                    return;
                }
                goto case UnaryOperatorKind.BitwiseNegation;

            case UnaryOperatorKind.BitwiseNegation:
                // ~x is x ^ -1 under the same numeric promotion rules, which keeps the
                // result type consistent with the binary path rather than inventing one here.
                if (BinaryOps.TryApply(eval, BinaryOperatorKind.ExclusiveOr, false, pv,
                        new PrimitiveValue(TypeCode.Int32, -1), out var notResult))
                {
                    eval.ValueStack.Push(notResult);
                    return;
                }
                break;

            case UnaryOperatorKind.Minus:
                // 0 - x, again reusing the promotion table.
                if (BinaryOps.TryApply(eval, BinaryOperatorKind.Subtract, _op.IsChecked,
                        new PrimitiveValue(TypeCode.Int32, 0), pv, out var negResult))
                {
                    eval.ValueStack.Push(negResult);
                    return;
                }
                break;
        }

        throw new NotSupportedException($"Unary operator {_op.OperatorKind} is not supported on this operand type.");
    }
}

/// `i++`, `++i`, `i--`, `--i`.
///
/// Postfix and prefix differ only in which value the expression yields; both write the same
/// updated value. As a statement the result is discarded, so the distinction only matters
/// inside a larger expression such as `a[i++] = v`.
public class IncrementCont : Continuation
{
    private readonly IIncrementOrDecrementOperation _op;
    public IncrementCont(IIncrementOrDecrementOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var targetRef = eval.RefStack.Pop();
        var oldValue = eval.ReadReference(targetRef);

        var kind = _op.Kind == OperationKind.Increment ? BinaryOperatorKind.Add : BinaryOperatorKind.Subtract;
        if (!BinaryOps.TryApply(eval, kind, _op.IsChecked, oldValue, new PrimitiveValue(TypeCode.Int32, 1), out var newValue))
        {
            throw new NotSupportedException($"{_op.Kind} is not supported on {oldValue.GetType().Name}.");
        }

        if (eval.UnwindingException != null) return;

        // Promotion widens `byte b` to int, so narrow back to the variable's own type or the
        // slot would silently change type after the first ++.
        if (_op.Target.Type is { } targetType &&
            BinaryOps.TryConvert(eval, newValue, targetType.SpecialType, _op.IsChecked, out var narrowed))
        {
            newValue = narrowed;
        }

        eval.WriteReference(targetRef, newValue);
        eval.ValueStack.Push(_op.IsPostfix ? oldValue : newValue);
    }
}

/// `x += y`, `x *= y`, and friends.
public class CompoundAssignCont : Continuation
{
    private readonly ICompoundAssignmentOperation _op;
    public CompoundAssignCont(ICompoundAssignmentOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var rhs = eval.ValueStack.Pop();
        var targetRef = eval.RefStack.Pop();
        var oldValue = eval.ReadReference(targetRef);

        if (!BinaryOps.TryApply(eval, _op.OperatorKind, _op.IsChecked, oldValue, rhs, out var newValue))
        {
            throw new NotSupportedException(
                $"Compound assignment {_op.OperatorKind} is not supported on " +
                $"{oldValue.GetType().Name} and {rhs.GetType().Name}.");
        }

        if (eval.UnwindingException != null) return;

        // C# defines `x op= y` as `x = (T)(x op y)`: the cast back to T is part of the
        // language, which is why `byte b = 200; b += 100;` compiles and wraps.
        if (_op.Target.Type is { } targetType &&
            BinaryOps.TryConvert(eval, newValue, targetType.SpecialType, _op.IsChecked, out var narrowed))
        {
            newValue = narrowed;
        }

        eval.WriteReference(targetRef, newValue);
        eval.ValueStack.Push(newValue);
    }
}

/// An explicit or implicit conversion.
///
/// Without this the operand passed through untouched, so `(int)3.7` stayed 3.7 and `1 / 2.0`
/// tried to divide an int by a double. Roslyn inserts a conversion node wherever the language
/// requires one, so honouring it here is what keeps arithmetic matching real C#.
public class ConvertCont : Continuation
{
    private readonly IConversionOperation _op;
    public ConvertCont(IConversionOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Pop();
        var target = _op.Type;

        if (target == null || value is NullValue or UnsetValue)
        {
            eval.ValueStack.Push(value);
            return;
        }

        // A reference conversion (upcast, downcast, interface) does not change the value; the
        // heap object already carries its own runtime type, which is what dispatch reads.
        if (target.SpecialType == SpecialType.None || value is ObjectRef or StructValue)
        {
            eval.ValueStack.Push(value);
            return;
        }

        // Enum-to-int and int-to-enum: enums are represented by their underlying primitive,
        // so the conversion is already done.
        if (target.TypeKind == TypeKind.Enum)
        {
            eval.ValueStack.Push(value);
            return;
        }

        if (!BinaryOps.TryConvert(eval, value, target.SpecialType, _op.IsChecked, out var converted))
        {
            throw new NotSupportedException(
                $"Cannot convert {value.GetType().Name} to {target.ToDisplayString()}.");
        }

        eval.ValueStack.Push(converted);
    }
}

/// `a ?? b` - evaluates the right side only when the left is null.
public class CoalesceCont : Continuation
{
    private readonly ICoalesceOperation _op;
    public CoalesceCont(ICoalesceOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Pop();
        if (value is NullValue)
        {
            eval.ContStack.Push(new EvalOperationCont(_op.WhenNull));
        }
        else
        {
            eval.ValueStack.Push(value);
        }
    }
}

/// `$"a{b}c"`.
///
/// Parts were pushed left to right, so they pop last-to-first; the builder walks backwards
/// for the same reason the array initialiser does.
public class InterpolatedStringCont : Continuation
{
    private readonly int _partCount;
    public InterpolatedStringCont(int partCount) => _partCount = partCount;

    public override void Execute(Evaluator eval)
    {
        var parts = new string[_partCount];
        for (int i = _partCount - 1; i >= 0; i--)
        {
            parts[i] = BinaryOps.Stringify(eval.ValueStack.Pop());
        }

        var sb = new StringBuilder();
        foreach (var part in parts) sb.Append(part);
        eval.ValueStack.Push(new PrimitiveValue(TypeCode.String, sb.ToString()));
    }
}

/// `x is SomeType`.
public class IsTypeCont : Continuation
{
    private readonly IIsTypeOperation _op;
    public IsTypeCont(IIsTypeOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Pop();
        bool result = false;

        if (value is ObjectRef objRef && eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj != null)
        {
            var runtimeType = eval.GetTypeSymbol(heapObj.TypeId);
            result = Evaluator.IsAssignableTo(runtimeType, _op.TypeOperand);
        }
        else if (value is StructValue sv)
        {
            result = Evaluator.IsAssignableTo(sv.Type, _op.TypeOperand);
        }

        if (_op.IsNegated) result = !result;
        eval.ValueStack.Push(new PrimitiveValue(TypeCode.Boolean, result));
    }
}

/// The lvalue half of `_ = expr`. Writing to it is a no-op by definition.
public sealed record DiscardRef : IReference
{
    public static readonly DiscardRef Instance = new();
}

/// Captures an indexer or property target for a later write.
public class PropertyLValueCont : Continuation
{
    private readonly IPropertyReferenceOperation _op;
    public PropertyLValueCont(IPropertyReferenceOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var args = new IValue[_op.Arguments.Length];
        for (int i = args.Length - 1; i >= 0; i--)
        {
            args[i] = eval.ValueStack.Pop();
        }

        var instance = _op.Instance != null ? eval.ValueStack.Pop() : NullValue.Instance;
        eval.RefStack.Push(new PropertyRef(_op.Property, instance, args));
    }
}
