using System;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;
using CsViz.Core.Heap;

namespace CsViz.Core.Eval;

public class ThrowCont : Continuation
{
    private readonly IThrowOperation _op;
    public ThrowCont(IThrowOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        if (_op.Exception != null)
        {
            var exceptionValue = eval.ValueStack.Pop();
            eval.UnwindingException = exceptionValue;
        }
        else
        {
            // Rethrow. We need to preserve the current unwinding exception, but wait, `throw;` can only happen inside a catch block.
            // If we are evaluating `throw;`, the exception should be bound to the catch block variable.
            // For now, assume it's just a raw exception that needs to be implemented.
            throw new NotImplementedException("Bare rethrow not implemented yet");
        }
    }
}

public class TryCatchCont : Continuation, IExceptionHandlerContinuation
{
    private readonly ITryOperation _op;
    public TryCatchCont(ITryOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var exception = eval.UnwindingException;
        if (exception == null)
        {
            // Executed normally, do nothing. Wait, this runs AFTER try block. So normal completion means we just pop this.
            return;
        }

        // We are unwinding. See if any catch block handles this exception.
        // In M2, we check if exception is an ObjectRef and if its ClassObject inherits from the catch type.
        // For simplicity, let's just catch all for now, or match on type.
        
        foreach (var catchClause in _op.Catches)
        {
            bool isMatch = false;
            if (catchClause.ExceptionType == null)
            {
                isMatch = true; // catch { }
            }
            else if (exception is BuiltinExceptionValue bev)
            {
                if (catchClause.ExceptionType.Name == bev.ExceptionName || catchClause.ExceptionType.Name == "Exception")
                {
                    isMatch = true;
                }
            }
            else if (exception is ObjectRef objRef && eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj is ClassObject clsObj)
            {
                var thrownType = eval.GetTypeSymbol(clsObj.TypeId);
                var current = thrownType;
                while (current != null)
                {
                    if (Microsoft.CodeAnalysis.SymbolEqualityComparer.Default.Equals(current, catchClause.ExceptionType))
                    {
                        isMatch = true;
                        break;
                    }
                    current = current.BaseType;
                }
            }

            if (!isMatch)
            {
                continue;
            }
            
            eval.UnwindingException = null; // Caught!
            
            // Scope in the exception variable if present
            if (catchClause.ExceptionDeclarationOrExpression is IVariableDeclaratorOperation varDecl)
            {
                if (eval.CurrentFrame.SlotMap.TryGetValue(varDecl.Symbol, out var slotId))
                {
                    eval.WriteReference(new LocalRef(eval.CurrentFrame.Id, slotId), exception);
                }
            }
            
            eval.ContStack.Push(new EvalOperationCont(catchClause.Handler));
            return;
        }
        
        // If no match, we are still unwinding. Push this back?
        // No, if no match, it just pops, and UnwindingException stays set, bubbling up!
    }
}

public class FinallyCont : Continuation, IExceptionHandlerContinuation
{
    private readonly IBlockOperation _finallyBlock;
    public FinallyCont(IBlockOperation finallyBlock) => _finallyBlock = finallyBlock;

    public override void Execute(Evaluator eval)
    {
        // Execute finally block. Wait, if we are unwinding, we execute finally block, 
        // then we need to RESUME unwinding.
        // How? We can push a "ResumeUnwindingCont" after the finally block!
        
        var ex = eval.UnwindingException;
        eval.UnwindingException = null; // Clear temporarily so finally runs normally
        
        if (ex != null)
        {
            eval.ContStack.Push(new ResumeUnwindCont(ex));
        }
        
        eval.ContStack.Push(new EvalOperationCont(_finallyBlock));
    }
}

public class ResumeUnwindCont : Continuation, IExceptionHandlerContinuation
{
    private readonly IValue _exception;
    public ResumeUnwindCont(IValue exception) => _exception = exception;

    public override void Execute(Evaluator eval)
    {
        eval.UnwindingException = _exception;
    }
}
