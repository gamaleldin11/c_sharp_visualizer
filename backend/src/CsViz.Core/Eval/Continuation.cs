using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using CsViz.Core.Values;

namespace CsViz.Core.Eval;

public abstract class Continuation
{
    public abstract void Execute(Evaluator eval);
}

public interface IExceptionHandlerContinuation
{
}

public class EvalOperationCont : Continuation
{
    public IOperation Operation { get; }
    public EvalOperationCont(IOperation op) => Operation = op;
    public override void Execute(Evaluator eval)
    {
        // An expression statement is already a step because its parent is the block; also
        // treating its child expression as a step emitted every assignment twice - once with
        // the delta, once empty.
        bool isStep = Operation.Parent is Microsoft.CodeAnalysis.Operations.IBlockOperation ||
                      Operation.Kind == OperationKind.VariableDeclaration;
                      
        if (isStep) 
        {
            eval.Recorder?.BeginStep(Operation, "stmt");
            eval.ContStack.Push(new EndStepCont());
        }
        
        eval.Visit(Operation);
    }
}

/// Closes the step opened by EvalOperationCont.
///
/// Marked as an exception-handler continuation so it still runs while an exception unwinds the
/// stack. Otherwise Run() would skip it and the encoder would be left with an open step whose
/// deltas leak into whichever step closes next - the throwing statement would simply vanish
/// from the trace, which is the one step a user most wants to see.
public class EndStepCont : Continuation, IExceptionHandlerContinuation
{
    public override void Execute(Evaluator eval)
    {
        eval.Recorder?.EndStep();
    }
}
