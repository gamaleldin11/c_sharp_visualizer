using System;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;

namespace CsViz.Core.Eval;

public class BinaryCombineCont : Continuation
{
    private readonly IBinaryOperation _op;
    public BinaryCombineCont(IBinaryOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var right = eval.ValueStack.Pop();
        var left = eval.ValueStack.Pop();

        if (_op.OperatorMethod != null)
        {
            throw new NotSupportedException(
                $"User-defined operator {_op.OperatorMethod.Name} is not supported.");
        }

        if (!BinaryOps.TryApply(eval, _op.OperatorKind, _op.IsChecked, left, right, out var result))
        {
            // Fail loudly. A silent fall-through here pushes nothing onto the value stack, so
            // the failure surfaces later as an unrelated stack underflow that is very hard to
            // trace back to its cause.
            throw new NotSupportedException(
                $"Unsupported binary operation {_op.OperatorKind} on operands " +
                $"{left.GetType().Name} and {right.GetType().Name}.");
        }

        // A fault (overflow, divide by zero) is already unwinding; pushing a result on top of
        // it would leave a stale value behind once a catch clause resumes.
        if (eval.UnwindingException == null) eval.ValueStack.Push(result);
    }
}

public class ConditionalBranchCont : Continuation
{
    private readonly IBinaryOperation _op;
    public ConditionalBranchCont(IBinaryOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var leftVal = eval.ValueStack.Pop();
        if (leftVal is PrimitiveValue { Value: bool leftBool })
        {
            if (_op.OperatorKind == BinaryOperatorKind.ConditionalAnd)
            {
                if (!leftBool)
                {
                    eval.ValueStack.Push(new PrimitiveValue(TypeCode.Boolean, false));
                }
                else
                {
                    eval.ContStack.Push(new ConditionalBranchResultCont());
                    eval.ContStack.Push(new EvalOperationCont(_op.RightOperand));
                }
            }
            else if (_op.OperatorKind == BinaryOperatorKind.ConditionalOr)
            {
                if (leftBool)
                {
                    eval.ValueStack.Push(new PrimitiveValue(TypeCode.Boolean, true));
                }
                else
                {
                    eval.ContStack.Push(new ConditionalBranchResultCont());
                    eval.ContStack.Push(new EvalOperationCont(_op.RightOperand));
                }
            }
        }
        else
        {
            throw new Exception("Left operand of conditional branch must be a boolean primitive");
        }
    }
}

public class ConditionalBranchResultCont : Continuation
{
    public override void Execute(Evaluator eval)
    {
        // Right operand value is already on top of the ValueStack.
    }
}

public class AssignWriteCont : Continuation
{
    private readonly ISimpleAssignmentOperation _op;
    public AssignWriteCont(ISimpleAssignmentOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Peek(); // Leave on stack as result of assignment expression
        var targetRef = eval.RefStack.Pop();
        
        eval.WriteReference(targetRef, value);
    }
}
