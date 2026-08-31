using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;
using CsViz.Core.Heap;
using CsViz.Core.Frames;
using CsViz.Core.Recorder;
using System.Linq;

namespace CsViz.Core.Eval;

public class Evaluator
{
    public InterpHeap Heap { get; } = new();
    public Stack<Frame> FrameStack { get; } = new();
    public Stack<Continuation> ContStack { get; } = new();
    public Stack<IValue> ValueStack { get; } = new();
    public Stack<IReference> RefStack { get; } = new();

    /// Objects currently being filled in by an object or collection initializer.
    ///
    /// A list rather than a Stack because writes address entries by index: a struct receiver
    /// is replaced wholesale on every field assignment, which Stack cannot express.
    public List<IValue> ImplicitReceivers { get; } = new();
    public ITraceRecorder? Recorder { get; set; }
    public IMethodProvider MethodProvider { get; }
    
    public IValue? UnwindingException { get; set; }
    
    private readonly Dictionary<ITypeSymbol, int> _typeToId = new(SymbolEqualityComparer.Default);
    private readonly List<ITypeSymbol> _idToType = new();

    public int GetTypeId(ITypeSymbol type)
    {
        if (_typeToId.TryGetValue(type, out var id)) return id;
        id = _idToType.Count;
        _idToType.Add(type);
        _typeToId[type] = id;
        return id;
    }

    public ITypeSymbol GetTypeSymbol(int id) => _idToType[id];

    private readonly Dictionary<INamedTypeSymbol, int> _staticsObjects = new(SymbolEqualityComparer.Default);

    /// A reference to a static field.
    ///
    /// Statics are stored as one ordinary heap object per declaring type, allocated on first
    /// touch. Modelling them this way rather than as a side table means the existing
    /// setField delta, the memory view, and the pointer arrows all work on them unchanged -
    /// and a static field holding a reference is drawn as an edge like any other.
    ///
    /// Only constant initialisers are honoured; a static field with a computed initialiser
    /// starts at its default value, because running static constructors would require a
    /// type-initialisation order this interpreter does not model.
    public IReference StaticFieldRef(IFieldSymbol field)
    {
        var declaring = field.ContainingType;
        if (!_staticsObjects.TryGetValue(declaring, out var objId))
        {
            var fields = new Dictionary<string, IValue>();
            foreach (var member in declaring.GetMembers().OfType<IFieldSymbol>())
            {
                if (!member.IsStatic || member.IsConst) continue;
                fields[member.Name] = member.HasConstantValue && member.ConstantValue is { } c
                    ? new PrimitiveValue(Type.GetTypeCode(c.GetType()), c)
                    : BinaryOps.DefaultFor(member.Type);
            }

            var obj = new ClassObject(GetTypeId(declaring), fields);
            objId = Heap.Allocate(obj);
            _staticsObjects[declaring] = objId;
            Recorder?.RecordNewObj(objId, obj);
        }

        return new FieldRef(objId, field.Name);
    }

    /// Continuations executed. This is interpreter work, not user-visible steps: one C#
    /// statement costs somewhere between a handful and a few dozen continuations.
    public int OperationCount { get; private set; }

    // Every limit exists to bound a specific way a user program can hurt the server, and each
    // one returns a clean limit_exceeded trace showing everything up to the cutoff rather than
    // an error page. A student's runaway loop should teach them something, not a 500.

    /// Ceiling on interpreter work.
    ///
    /// Deliberately much larger than the trace-step cap the encoder enforces. Conflating the
    /// two made every recursion test trip the "steps" limit before the stack-depth limit it was
    /// meant to demonstrate, because a single recursive call costs a dozen continuations.
    public int MaxOperations { get; set; } = 500_000;

    /// Bounds memory. A loop allocating in a tight cycle would otherwise exhaust the process.
    public int MaxHeapObjects { get; set; } = 5000;

    /// Bounds runaway recursion. Because the interpreter uses an explicit continuation stack
    /// rather than host recursion, this is a policy number and not a hard technical ceiling -
    /// deep recursion reports cleanly instead of crashing the server with a StackOverflow,
    /// which is the entire reason for that design.
    public int MaxFrameDepth { get; set; } = 2000;

    /// Bounds output. 64KB is far more than anything readable and far less than anything that
    /// would trouble a browser.
    public int MaxOutputChars { get; set; } = 64 * 1024;

    /// Wall-clock ceiling. The step budget bounds ordinary work, but a single pathological
    /// expression can be slow enough that steps alone do not bound latency.
    public TimeSpan TimeBudget { get; set; } = TimeSpan.FromSeconds(3);

    /// Characters written to stdout so far, maintained by the recorder.
    public int OutputChars { get; set; }

    private void CheckLimits(System.Diagnostics.Stopwatch clock)
    {
        // Depth first: a runaway recursion should be reported as what it is, not as a generic
        // "too much work", which is what the reader needs to see.
        if (FrameStack.Count > MaxFrameDepth) throw new LimitExceededException("stackDepth");
        if (Heap.Count > MaxHeapObjects) throw new LimitExceededException("heap");
        if (OutputChars > MaxOutputChars) throw new LimitExceededException("output");
        if (OperationCount > MaxOperations) throw new LimitExceededException("steps");

        // Checked periodically rather than every iteration: Stopwatch.Elapsed is not free, and
        // 512 continuations is far too short an interval to overshoot the budget noticeably.
        if ((OperationCount & 511) == 0 && clock.Elapsed > TimeBudget) throw new LimitExceededException("time");
    }

    private string[] _stdinLines = Array.Empty<string>();
    private int _stdinPos;

    /// Supplies the lines Console.ReadLine will return, in order.
    public void SetStdin(string? stdin)
    {
        _stdinLines = string.IsNullOrEmpty(stdin)
            ? Array.Empty<string>()
            : stdin.Replace("\r\n", "\n").Split('\n');
        _stdinPos = 0;
    }

    /// Returns null once input is exhausted, exactly as Console.ReadLine does at end of stream.
    public string? ReadStdinLine() => _stdinPos < _stdinLines.Length ? _stdinLines[_stdinPos++] : null;
    
    public Evaluator(IMethodProvider methodProvider)
    {
        MethodProvider = methodProvider;
    }
    
    public Frame CurrentFrame => FrameStack.Peek();
    
    public void PushFrame(Frame frame)
    {
        FrameStack.Push(frame);
        Recorder?.RecordPushFrame(frame);
    }
    
    public void PopFrame()
    {
        FrameStack.Pop();
        Recorder?.RecordPopFrame();
    }
    
    public void Run(IOperation rootOp)
    {
        ContStack.Push(new EvalOperationCont(rootOp));

        var clock = System.Diagnostics.Stopwatch.StartNew();

        while (ContStack.Count > 0)
        {
            var cont = ContStack.Pop();

            if (UnwindingException != null && !(cont is IExceptionHandlerContinuation))
            {
                // Discard continuations until we hit an exception handler
                continue;
            }

            cont.Execute(this);

            OperationCount++;
            CheckLimits(clock);
        }
        
        if (UnwindingException != null)
        {
            // An uncaught exception is a normal outcome for a program a learner is debugging,
            // not a failure of the interpreter. It is raised as its own type so the API can
            // report it as the C# exception it is, with a message the user recognises, rather
            // than as an internal error.
            throw UnhandledUserException.From(UnwindingException, this);
        }
    }

    public IValue ReadReference(IReference reference)
    {
        switch (reference)
        {
            case LocalRef lr:
                var targetFrame = FrameStack.FirstOrDefault(f => f.Id == lr.FrameId);
                return targetFrame?.Slots.Find(s => s.SlotId == lr.SlotId)?.Value ?? UnsetValue.Instance;
                
            case ArrayElemRef ar:
                if (Heap.TryGet(ar.ObjId, out var obj) && obj is ArrayObject arrayObj)
                {
                    if (ar.Indices[0] < 0 || ar.Indices[0] >= arrayObj.Elems.Length)
                    {
                        // A C#-level fault the user can catch, not an interpreter failure.
                        // Letting the host IndexOutOfRangeException escape aborted the whole
                        // trace and reported it as an internal error.
                        UnwindingException = new BuiltinExceptionValue("IndexOutOfRangeException");
                        return UnsetValue.Instance;
                    }
                    return arrayObj.Elems[ar.Indices[0]];
                }
                throw new NotSupportedException("Array element reference on a non-array value.");
                
            case StructFieldRef sfr:
                var parentValue = ReadReference(sfr.Parent);
                if (parentValue is StructValue sv && sv.Fields.TryGetValue(sfr.FieldName, out var val))
                    return val;
                throw new Exception("Invalid struct field reference");
                
            case FieldRef fr:
                if (Heap.TryGet(fr.ObjId, out var fobj) && fobj is ClassObject cls && cls.Fields.TryGetValue(fr.Name, out var fval))
                    return fval;
                throw new Exception("Invalid field reference");

            case PropertyRef pr:
                if (pr.Property.GetMethod is { } getter &&
                    Bridge.BCLBridge.TryInvoke(getter, pr.Instance, pr.Arguments, this, out var got))
                {
                    return got;
                }
                throw new NotSupportedException(
                    $"Cannot read property '{pr.Property.Name}' as part of an assignment.");

            case ThisRef tr:
                return FrameStack.FirstOrDefault(f => f.Id == tr.FrameId)?.ThisValue ?? UnsetValue.Instance;

            case ReceiverRef rr:
                return ImplicitReceivers[rr.Index];

            case DiscardRef:
                return UnsetValue.Instance;

            default:
                throw new NotSupportedException($"Unsupported reference type {reference.GetType().Name}.");
        }
    }

    public void WriteReference(IReference reference, IValue value)
    {
        switch (reference)
        {
            case LocalRef lr:
                var targetFrame = FrameStack.FirstOrDefault(f => f.Id == lr.FrameId);
                if (targetFrame != null)
                {
                    var slot = targetFrame.Slots.Find(s => s.SlotId == lr.SlotId);
                    if (slot != null) slot.Value = value;
                    Recorder?.RecordSetLocal(lr.FrameId, lr.SlotId, value);
                }
                break;
                
            case ArrayElemRef ar:
                if (Heap.TryGet(ar.ObjId, out var obj) && obj is ArrayObject arrayObj)
                {
                    if (ar.Indices[0] < 0 || ar.Indices[0] >= arrayObj.Elems.Length)
                    {
                        UnwindingException = new BuiltinExceptionValue("IndexOutOfRangeException");
                        break;
                    }
                    arrayObj.Elems[ar.Indices[0]] = value;
                    Recorder?.RecordSetElem(ar.ObjId, ar.Indices[0], value);
                }
                break;
                
            case FieldRef fr:
                if (Heap.TryGet(fr.ObjId, out var fobj) && fobj is ClassObject cls)
                {
                    cls.Fields[fr.Name] = value;
                    Recorder?.RecordSetField(fr.ObjId, fr.Name, value);
                }
                break;

            case StructFieldRef sfr:
                var parentValue = ReadReference(sfr.Parent);
                if (parentValue is StructValue sv)
                {
                    // Structs are immutable records here, so mutating a field means rebuilding
                    // the struct and writing it back through its own reference. That recursion
                    // is what makes `a.b.c = 1` on nested structs update `a` rather than a copy.
                    var newFields = sv.Fields.SetItem(sfr.FieldName, value);
                    WriteReference(sfr.Parent, new StructValue(sv.Type, newFields));
                }
                break;

            case PropertyRef pr:
                if (pr.Property.SetMethod is { } setter)
                {
                    // The setter takes the index arguments followed by the value.
                    var setterArgs = new IValue[pr.Arguments.Length + 1];
                    Array.Copy(pr.Arguments, setterArgs, pr.Arguments.Length);
                    setterArgs[^1] = value;

                    if (Bridge.BCLBridge.TryInvoke(setter, pr.Instance, setterArgs, this, out _)) break;

                    // A user-declared property: run its setter body like any other method.
                    // The value is pushed last because that is the parameter order above.
                    ValueStack.Push(pr.Instance);
                    foreach (var arg in setterArgs) ValueStack.Push(arg);
                    ContStack.Push(new DiscardResultCont());
                    ContStack.Push(new MethodCallCont(setter, hasInstance: true, argCount: setterArgs.Length));
                    break;
                }
                throw new NotSupportedException($"Property '{pr.Property.Name}' has no setter.");

            case ThisRef tr:
                var thisFrame = FrameStack.FirstOrDefault(f => f.Id == tr.FrameId);
                if (thisFrame != null) thisFrame.ThisValue = value;
                break;

            case ReceiverRef rr:
                ImplicitReceivers[rr.Index] = value;
                break;

            case DiscardRef:
                // `_ = expr` evaluates the right side and throws the result away.
                break;

            default:
                throw new NotSupportedException($"Cannot write to reference of type {reference.GetType().Name}.");
        }
    }
    
    public void Visit(IOperation op)
    {
        if (op == null) return;
        
        switch (op.Kind)
        {
            case OperationKind.Literal:
                var literal = (ILiteralOperation)op;
                // A null literal is NullValue, never a PrimitiveValue wrapping null. Wrapping it
                // produces PrimitiveValue(Object, NullValue) which no comparison path recognises.
                if (literal.ConstantValue.Value is null)
                {
                    ValueStack.Push(NullValue.Instance);
                }
                else
                {
                    ValueStack.Push(new PrimitiveValue(Type.GetTypeCode(literal.ConstantValue.Value.GetType()), literal.ConstantValue.Value));
                }
                break;
                
            case OperationKind.LocalReference:
                var localRef = (ILocalReferenceOperation)op;
                if (CurrentFrame.SlotMap.TryGetValue(localRef.Local, out var slotId))
                {
                    var slot = CurrentFrame.Slots.Find(s => s.SlotId == slotId);
                    if (slot != null)
                    {
                        if (slot.Value is UnsetValue) throw new Exception("Internal error: Unset local read");
                        ValueStack.Push(slot.Value);
                    }
                    else
                    {
                        ValueStack.Push(UnsetValue.Instance);
                    }
                }
                else
                {
                    ValueStack.Push(UnsetValue.Instance);
                }
                break;
                
            case OperationKind.ParameterReference:
                var paramRef = (IParameterReferenceOperation)op;
                if (CurrentFrame.SlotMap.TryGetValue(paramRef.Parameter, out var paramSlotId))
                {
                    var slot = CurrentFrame.Slots.Find(s => s.SlotId == paramSlotId);
                    if (slot != null)
                    {
                        ValueStack.Push(slot.Value);
                    }
                    else
                    {
                        ValueStack.Push(UnsetValue.Instance);
                    }
                }
                else
                {
                    ValueStack.Push(UnsetValue.Instance);
                }
                break;
                
            case OperationKind.ExpressionStatement:
                var exprStmt = (IExpressionStatementOperation)op;
                ContStack.Push(new DiscardResultCont());
                ContStack.Push(new EvalOperationCont(exprStmt.Operation));
                break;
                
            case OperationKind.Block:
                var block = (IBlockOperation)op;
                ContStack.Push(new ExitBlockScopeCont(block));
                for (int i = block.Operations.Length - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(block.Operations[i]));
                }
                ContStack.Push(new EnterBlockScopeCont(block));
                break;
                
            case OperationKind.Binary:
                var binOp = (IBinaryOperation)op;
                if (binOp.OperatorKind == BinaryOperatorKind.ConditionalAnd || binOp.OperatorKind == BinaryOperatorKind.ConditionalOr)
                {
                    ContStack.Push(new ConditionalBranchCont(binOp));
                    ContStack.Push(new EvalOperationCont(binOp.LeftOperand));
                }
                else
                {
                    ContStack.Push(new BinaryCombineCont(binOp));
                    ContStack.Push(new EvalOperationCont(binOp.RightOperand));
                    ContStack.Push(new EvalOperationCont(binOp.LeftOperand));
                }
                break;
                
            case OperationKind.SimpleAssignment:
                var assign = (ISimpleAssignmentOperation)op;
                ContStack.Push(new AssignWriteCont(assign));
                ContStack.Push(new EvalOperationCont(assign.Value));
                ContStack.Push(new EvalLValueCont(assign.Target));
                break;
                
            case OperationKind.VariableDeclarationGroup:
                var varGrp = (IVariableDeclarationGroupOperation)op;
                for (int i = varGrp.Declarations.Length - 1; i >= 0; i--)
                {
                    var decl = varGrp.Declarations[i];
                    for (int j = decl.Declarators.Length - 1; j >= 0; j--)
                    {
                        var declarator = decl.Declarators[j];
                        if (declarator.Initializer != null)
                        {
                            ContStack.Push(new DiscardResultCont());
                            ContStack.Push(new VarInitWriteCont(declarator));
                            ContStack.Push(new EvalOperationCont(declarator.Initializer.Value));
                        }
                    }
                }
                break;
                
            case OperationKind.VariableDeclarator:
                var varDeclNode = (IVariableDeclaratorOperation)op;
                if (varDeclNode.Initializer != null)
                {
                    ContStack.Push(new DiscardResultCont());
                    ContStack.Push(new VarInitWriteCont(varDeclNode));
                    ContStack.Push(new EvalOperationCont(varDeclNode.Initializer.Value));
                }
                break;
                
            case OperationKind.Conditional:
                var cond = (IConditionalOperation)op;
                ContStack.Push(new ConditionalCont(cond));
                ContStack.Push(new EvalOperationCont(cond.Condition));
                break;
                
            case OperationKind.Loop:
                if (op is IWhileLoopOperation whileLoop)
                {
                    ContStack.Push(new LoopStartCont(whileLoop));
                }
                else if (op is IForLoopOperation forLoop)
                {
                    // Loop variables are pre-declared at frame push; bring them into scope here
                    // so a re-entered loop starts from a clean slot.
                    EnterScope(forLoop.Locals);

                    // Push the loop first so the initialisers, pushed after, run before it.
                    ContStack.Push(new LoopStartCont(forLoop));
                    for (int i = forLoop.Before.Length - 1; i >= 0; i--)
                    {
                        ContStack.Push(new EvalOperationCont(forLoop.Before[i]));
                    }
                }
                else if (op is IForEachLoopOperation forEach)
                {
                    EnterScope(forEach.Locals);
                    ContStack.Push(new ExitForEachScopeCont(forEach));
                    ContStack.Push(new ForEachStartCont(forEach));
                    ContStack.Push(new EvalOperationCont(forEach.Collection));
                }
                else
                {
                    throw new NotImplementedException($"Other loops not implemented: {op.GetType().Name}");
                }
                break;
                
            case OperationKind.Invocation:
                var invocation = (IInvocationOperation)op;
                ContStack.Push(new InvocationOpCont(invocation));
                for (int i = invocation.Arguments.Length - 1; i >= 0; i--)
                {
                    var argument = invocation.Arguments[i];
                    if (IsByReference(argument))
                    {
                        // A ref/out argument names a storage location, not a value. Two things
                        // are needed: the reference, so the callee's final value can be copied
                        // back, and one value on the stack so argument positions still line up.
                        //
                        // `out` deliberately contributes Unset rather than reading the target:
                        // C# does not require an out argument to be assigned beforehand, and
                        // reading it would fault on a legitimate program.
                        ContStack.Push(argument.Parameter?.RefKind == RefKind.Out
                            ? new PushUnsetCont()
                            : new EvalOperationCont(argument.Value));
                        ContStack.Push(new EvalLValueCont(argument.Value));
                        continue;
                    }
                    ContStack.Push(new EvalOperationCont(argument.Value));
                }
                if (invocation.Instance != null)
                {
                    ContStack.Push(new EvalOperationCont(invocation.Instance));
                }
                break;
                
            case OperationKind.Return:
                var retOp = (IReturnOperation)op;
                ContStack.Push(new ReturnCont());
                if (retOp.ReturnedValue != null)
                {
                    ContStack.Push(new EvalOperationCont(retOp.ReturnedValue));
                }
                break;
                
            case OperationKind.ArrayCreation:
                var arrOp = (IArrayCreationOperation)op;
                if (arrOp.DimensionSizes.Length > 1)
                {
                    // Reported here rather than failing later with "not an array": the
                    // downstream message named the wrong cause entirely, which is worse than
                    // no message at all.
                    throw new UnsupportedConstructException(op,
                        "- multi-dimensional arrays are not supported, but jagged arrays (int[][]) are");
                }
                if (arrOp.Initializer != null)
                {
                    ContStack.Push(new ArrayInitCont(arrOp));
                    for (int i = arrOp.Initializer.ElementValues.Length - 1; i >= 0; i--)
                    {
                        ContStack.Push(new EvalOperationCont(arrOp.Initializer.ElementValues[i]));
                    }
                }
                else
                {
                    ContStack.Push(new ArrayCreateEmptyCont(arrOp));
                    for (int i = arrOp.DimensionSizes.Length - 1; i >= 0; i--)
                    {
                        ContStack.Push(new EvalOperationCont(arrOp.DimensionSizes[i]));
                    }
                }
                break;
                
            case OperationKind.ArrayElementReference:
                var arrElem = (IArrayElementReferenceOperation)op;
                ContStack.Push(new ArrayElemReadCont());
                for (int i = arrElem.Indices.Length - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(arrElem.Indices[i]));
                }
                ContStack.Push(new EvalOperationCont(arrElem.ArrayReference));
                break;
                
            case OperationKind.PropertyReference:
                var propRef = (IPropertyReferenceOperation)op;
                // Treat property read as an invocation of the getter
                if (propRef.Property.GetMethod == null) throw new Exception("Property has no getter");
                ContStack.Push(new MethodCallCont(propRef.Property.GetMethod, propRef.Instance != null, propRef.Arguments.Length, propRef.Property.GetMethod.IsVirtual));
                for (int i = propRef.Arguments.Length - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(propRef.Arguments[i].Value));
                }
                if (propRef.Instance != null)
                {
                    ContStack.Push(new EvalOperationCont(propRef.Instance));
                }
                break;
                
            case OperationKind.ObjectCreation:
                var objCreation = (IObjectCreationOperation)op;
                ContStack.Push(new ObjectCreationCont(objCreation));
                for (int i = objCreation.Arguments.Length - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(objCreation.Arguments[i].Value));
                }
                break;

            case OperationKind.FieldReference:
                var fieldRef = (IFieldReferenceOperation)op;
                ContStack.Push(new FieldReadCont(fieldRef));
                if (fieldRef.Instance != null)
                {
                    ContStack.Push(new EvalOperationCont(fieldRef.Instance));
                }
                break;

            case OperationKind.Throw:
                var throwOp = (IThrowOperation)op;
                ContStack.Push(new ThrowCont(throwOp));
                if (throwOp.Exception != null)
                {
                    ContStack.Push(new EvalOperationCont(throwOp.Exception));
                }
                break;
                
            case OperationKind.Try:
                var tryOp = (ITryOperation)op;
                if (tryOp.Finally != null)
                {
                    ContStack.Push(new FinallyCont(tryOp.Finally));
                }
                if (tryOp.Catches.Length > 0)
                {
                    ContStack.Push(new TryCatchCont(tryOp));
                }
                ContStack.Push(new EvalOperationCont(tryOp.Body));
                break;
                
            case OperationKind.Conversion:
                var conv = (IConversionOperation)op;
                bool isBoxing = conv.Operand.Type?.IsValueType == true && conv.Type?.IsReferenceType == true;
                if (isBoxing && !FeedsStringConversion(conv))
                {
                    ContStack.Push(new BoxingCont(conv));
                }
                else
                {
                    // Numeric conversions genuinely change the value. Passing the operand
                    // through unchanged left `(int)3.7` as 3.7 and made `1 / 2.0` an int/double
                    // division that the operator table has no entry for.
                    ContStack.Push(new ConvertCont(conv));
                }
                ContStack.Push(new EvalOperationCont(conv.Operand));
                break;

            case OperationKind.Unary:
                var unary = (IUnaryOperation)op;
                if (unary.OperatorMethod != null)
                    throw new NotSupportedException("User-defined operators are not supported.");
                ContStack.Push(new UnaryCont(unary));
                ContStack.Push(new EvalOperationCont(unary.Operand));
                break;

            case OperationKind.Increment:
            case OperationKind.Decrement:
                var incr = (IIncrementOrDecrementOperation)op;
                ContStack.Push(new IncrementCont(incr));
                ContStack.Push(new EvalLValueCont(incr.Target));
                break;

            case OperationKind.CompoundAssignment:
                var compound = (ICompoundAssignmentOperation)op;
                if (compound.OperatorMethod != null)
                    throw new NotSupportedException("User-defined operators are not supported.");
                ContStack.Push(new CompoundAssignCont(compound));
                ContStack.Push(new EvalOperationCont(compound.Value));
                ContStack.Push(new EvalLValueCont(compound.Target));
                break;

            case OperationKind.Switch:
                var switchOp = (ISwitchOperation)op;
                EnterScope(switchOp.Locals);
                ContStack.Push(new SwitchDispatchCont(switchOp));
                ContStack.Push(new EvalOperationCont(switchOp.Value));
                break;

            case OperationKind.Branch:
                ContStack.Push(new BranchCont((IBranchOperation)op));
                break;

            case OperationKind.InstanceReference:
                // Inside an object initializer, `this` is the object being built, not the
                // enclosing method's receiver.
                var instRef = (IInstanceReferenceOperation)op;
                ValueStack.Push(instRef.ReferenceKind == InstanceReferenceKind.ImplicitReceiver && ImplicitReceivers.Count > 0
                    ? ImplicitReceivers[^1]
                    : CurrentFrame.ThisValue);
                break;

            case OperationKind.InterpolatedString:
                var interp = (IInterpolatedStringOperation)op;
                var evaluable = new List<IOperation>();
                foreach (var part in interp.Parts)
                {
                    // A text part is a literal; an interpolation hole is an expression, and
                    // only its value expression is evaluated - alignment and format specifiers
                    // are not supported and are reported below rather than silently dropped.
                    switch (part)
                    {
                        case IInterpolatedStringTextOperation text:
                            evaluable.Add(text.Text);
                            break;
                        case IInterpolationOperation hole:
                            if (hole.Alignment != null || hole.FormatString != null)
                                throw new NotSupportedException(
                                    "Alignment and format specifiers in interpolated strings are not supported.");
                            evaluable.Add(hole.Expression);
                            break;
                        default:
                            throw new NotSupportedException($"Unsupported interpolated string part: {part.Kind}");
                    }
                }
                ContStack.Push(new InterpolatedStringCont(evaluable.Count));
                for (int i = evaluable.Count - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(evaluable[i]));
                }
                break;

            case OperationKind.Coalesce:
                var coalesce = (ICoalesceOperation)op;
                ContStack.Push(new CoalesceCont(coalesce));
                ContStack.Push(new EvalOperationCont(coalesce.Value));
                break;

            case OperationKind.IsType:
                var isType = (IIsTypeOperation)op;
                ContStack.Push(new IsTypeCont(isType));
                ContStack.Push(new EvalOperationCont(isType.ValueOperand));
                break;

            case OperationKind.DefaultValue:
                ValueStack.Push(BinaryOps.DefaultFor(op.Type));
                break;

            case OperationKind.NameOf:
            case OperationKind.SizeOf:
                // Both are compile-time constants, so Roslyn has already folded the answer.
                if (op.ConstantValue.HasValue && op.ConstantValue.Value is { } nameOfValue)
                {
                    ValueStack.Push(new PrimitiveValue(Type.GetTypeCode(nameOfValue.GetType()), nameOfValue));
                    break;
                }
                throw new NotSupportedException($"{op.Kind} could not be evaluated as a constant.");

            case OperationKind.Empty:
                break;

            case OperationKind.Discard:
                ValueStack.Push(NullValue.Instance);
                break;

            case OperationKind.Parenthesized:
                ContStack.Push(new EvalOperationCont(((IParenthesizedOperation)op).Operand));
                break;

            default:
                // Fail loudly with the source span. A visualizer that quietly renders a wrong
                // diagram is worse than one that declines to run.
                throw new UnsupportedConstructException(op);
        }
    }
    
    public void VisitLValue(IOperation op)
    {
        switch (op.Kind)
        {
            case OperationKind.LocalReference:
                var localRef = (ILocalReferenceOperation)op;
                if (CurrentFrame.SlotMap.TryGetValue(localRef.Local, out var slotId))
                {
                    RefStack.Push(new LocalRef(CurrentFrame.Id, slotId));
                }
                else
                {
                    throw new Exception("Local not found for lvalue");
                }
                break;
                
            case OperationKind.ParameterReference:
                var paramRef = (IParameterReferenceOperation)op;
                if (CurrentFrame.SlotMap.TryGetValue(paramRef.Parameter, out var paramSlotId))
                {
                    RefStack.Push(new LocalRef(CurrentFrame.Id, paramSlotId));
                }
                else
                {
                    throw new Exception("Parameter not found for lvalue");
                }
                break;
                
            case OperationKind.ArrayElementReference:
                var arrElemRef = (IArrayElementReferenceOperation)op;
                ContStack.Push(new ArrayElemLValueCont());
                for (int i = arrElemRef.Indices.Length - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(arrElemRef.Indices[i]));
                }
                ContStack.Push(new EvalOperationCont(arrElemRef.ArrayReference));
                break;
                
            case OperationKind.FieldReference:
                var fieldLVal = (IFieldReferenceOperation)op;
                ContStack.Push(new FieldLValueCont(fieldLVal));
                if (fieldLVal.Instance != null)
                {
                    if (fieldLVal.Instance.Type?.IsValueType == true)
                    {
                        ContStack.Push(new EvalLValueCont(fieldLVal.Instance));
                    }
                    else
                    {
                        ContStack.Push(new EvalOperationCont(fieldLVal.Instance));
                    }
                }
                break;
                
            case OperationKind.Discard:
                RefStack.Push(DiscardRef.Instance);
                break;

            case OperationKind.InstanceReference:
                // Reached for a struct: `this.X = v` inside a struct constructor or method
                // mutates the receiver in place, so the write has to land back on the frame's
                // `this` rather than on a copy of it - or on the object an initializer is
                // currently building.
                var instLValue = (IInstanceReferenceOperation)op;
                RefStack.Push(instLValue.ReferenceKind == InstanceReferenceKind.ImplicitReceiver && ImplicitReceivers.Count > 0
                    ? new ReceiverRef(ImplicitReceivers.Count - 1)
                    : new ThisRef(CurrentFrame.Id));
                break;

            case OperationKind.PropertyReference:
                // Indexers and auto-properties. The operands are evaluated now and captured in
                // the reference; the setter runs later, when the value is available.
                var propLValue = (IPropertyReferenceOperation)op;
                ContStack.Push(new PropertyLValueCont(propLValue));
                for (int i = propLValue.Arguments.Length - 1; i >= 0; i--)
                {
                    ContStack.Push(new EvalOperationCont(propLValue.Arguments[i].Value));
                }
                if (propLValue.Instance != null)
                {
                    ContStack.Push(new EvalOperationCont(propLValue.Instance));
                }
                break;

            case OperationKind.Parenthesized:
                ContStack.Push(new EvalLValueCont(((IParenthesizedOperation)op).Operand));
                break;

            default:
                throw new UnsupportedConstructException(op, "as an assignment target");
        }
    }

    /// Brings a scope's locals in and resets them.
    ///
    /// Slots are pre-declared at frame push so the trace always carries a name, but a scope
    /// that is entered twice - a loop body, a re-entered block - must start from Unset again
    /// or the second pass would show the first pass's leftovers.
    public void EnterScope(System.Collections.Immutable.ImmutableArray<ILocalSymbol> locals)
    {
        foreach (var local in locals)
        {
            if (!CurrentFrame.SlotMap.TryGetValue(local, out var slotId))
            {
                slotId = CurrentFrame.Slots.Count + 1;
                CurrentFrame.SlotMap[local] = slotId;
                CurrentFrame.Slots.Add(new Slot
                {
                    SlotId = slotId,
                    Name = local.Name,
                    Kind = SlotKind.Local,
                    InScope = true,
                    Value = UnsetValue.Instance
                });
            }
            else
            {
                var slot = CurrentFrame.Slots.Find(s => s.SlotId == slotId);
                if (slot != null)
                {
                    slot.InScope = true;
                    slot.Value = UnsetValue.Instance;
                }
            }
            Recorder?.RecordScope(CurrentFrame.Id, slotId, true);
        }
    }

    /// Discards continuations up to a `break`/`continue` target.
    ///
    /// Scope-exit and step-end continuations are still executed on the way out. Skipping them
    /// would leave locals marked in-scope for the rest of the trace and leave the encoder with
    /// an unclosed step, so a single `break` would corrupt everything after it.
    public void UnwindToLabel(ILabelSymbol? target, bool inclusive)
    {
        while (ContStack.Count > 0)
        {
            if (ContStack.Peek() is IBranchTarget bt &&
                SymbolEqualityComparer.Default.Equals(bt.Label, target))
            {
                if (inclusive) ContStack.Pop();
                return;
            }

            var cont = ContStack.Pop();
            if (cont is ExitBlockScopeCont or ExitForEachScopeCont or EndStepCont)
            {
                cont.Execute(this);
            }
            else if (cont is PopFrameCont)
            {
                // break/continue cannot cross a method boundary; reaching one means the
                // target label was never on this frame's stack.
                ContStack.Push(cont);
                throw new InvalidOperationException(
                    $"Could not find branch target '{target?.Name}' within the current method.");
            }
        }

        throw new InvalidOperationException($"Branch target '{target?.Name}' was not found on the stack.");
    }

    /// Does this boxing conversion exist only so the value can be turned into text?
    ///
    /// C# defines `string + object`, so `"n=" + i` formally boxes i. Honouring that literally
    /// put a heap object on the diagram for every such concatenation - a loop printing its
    /// counter would bury the user's real objects under hundreds of one-int boxes. Real .NET
    /// does not surface those either: they are an implementation detail of Concat. Boxing that
    /// the user can actually observe - `object o = 5;` - still allocates, because there the box
    /// is the thing worth seeing.
    private static bool FeedsStringConversion(IConversionOperation conv)
    {
        return conv.Parent switch
        {
            IBinaryOperation { OperatorKind: BinaryOperatorKind.Add } b =>
                b.Type?.SpecialType == SpecialType.System_String,
            // `s += 1` on a string is the same concatenation, reached through a different node.
            ICompoundAssignmentOperation { OperatorKind: BinaryOperatorKind.Add } c =>
                c.Type?.SpecialType == SpecialType.System_String,
            IInterpolationOperation => true,
            _ => false
        };
    }

    /// Does this argument pass a storage location rather than a value?
    ///
    /// `in` is included: it is a read-only reference, and copying its value back is harmless
    /// because the callee cannot have changed it.
    public static bool IsByReference(IArgumentOperation argument) =>
        argument.Parameter?.RefKind is RefKind.Ref or RefKind.Out or RefKind.In;

    /// Is `from` the same type as, or derived from / implementing, `to`?
    public static bool IsAssignableTo(ITypeSymbol? from, ITypeSymbol? to)
    {
        if (from == null || to == null) return false;

        for (var current = from; current != null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, to)) return true;
        }

        foreach (var iface in from.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, to)) return true;
        }

        return false;
    }

    /// The KeyValuePair<K,V> type a Dictionary's entries present as when enumerated.
    public INamedTypeSymbol KeyValuePairType(Heap.DictObject dict)
    {
        var dictType = GetTypeSymbol(dict.TypeId);
        if (dictType is INamedTypeSymbol { TypeArguments.Length: 2 } named)
        {
            var kvp = named.ContainingAssembly?.GetTypeByMetadataName("System.Collections.Generic.KeyValuePair`2")
                   ?? named.ContainingType;
            if (kvp is INamedTypeSymbol kvpNamed && kvpNamed.IsGenericType)
            {
                return kvpNamed.Construct(named.TypeArguments[0], named.TypeArguments[1]);
            }
        }
        // Falling back to the dictionary's own symbol keeps the struct renderable; only its
        // displayed type name is wrong, and never its Key/Value contents.
        return (INamedTypeSymbol)dictType;
    }
}

/// Raised when the interpreter meets a construct it does not implement.
///
/// Carries the source span so the API can turn it into a diagnostic pointing at the exact
/// text, rather than a bare "something went wrong".
public class UnsupportedConstructException : Exception
{
    public IOperation Operation { get; }
    public int Line { get; }
    public int Column { get; }
    public int EndLine { get; }
    public int EndColumn { get; }

    public UnsupportedConstructException(IOperation op, string context = "")
        : base(BuildMessage(op, context))
    {
        Operation = op;
        var span = op.Syntax.GetLocation().GetLineSpan();
        Line = span.StartLinePosition.Line + 1;
        Column = span.StartLinePosition.Character + 1;
        EndLine = span.EndLinePosition.Line + 1;
        EndColumn = span.EndLinePosition.Character + 1;
    }

    private static string BuildMessage(IOperation op, string context)
    {
        var text = op.Syntax.ToString();
        if (text.Length > 60) text = text[..60] + "...";
        var where = string.IsNullOrEmpty(context) ? "" : " " + context;
        return $"'{text}' uses a C# construct this visualizer does not support{where} ({op.Kind}).";
    }
}

public class EvalLValueCont : Continuation
{
    public IOperation Operation { get; }
    public EvalLValueCont(IOperation op) => Operation = op;
    public override void Execute(Evaluator eval) => eval.VisitLValue(Operation);
}

/// Contributes an unassigned value for an `out` argument, keeping argument positions aligned
/// without reading a variable the caller was never required to assign.
public class PushUnsetCont : Continuation
{
    public override void Execute(Evaluator eval) => eval.ValueStack.Push(UnsetValue.Instance);
}

public class DiscardResultCont : Continuation
{
    public override void Execute(Evaluator eval)
    {
        if (eval.ValueStack.Count > 0) eval.ValueStack.Pop();
    }
}

public class EnterBlockScopeCont : Continuation
{
    private readonly IBlockOperation _block;
    public EnterBlockScopeCont(IBlockOperation block) => _block = block;
    public override void Execute(Evaluator eval)
    {
        foreach (var local in _block.Locals)
        {
            if (!eval.CurrentFrame.SlotMap.ContainsKey(local))
            {
                int newSlotId = eval.CurrentFrame.Slots.Count + 1;
                eval.CurrentFrame.SlotMap[local] = newSlotId;
                eval.CurrentFrame.Slots.Add(new Slot
                {
                    SlotId = newSlotId,
                    Name = local.Name,
                    Kind = SlotKind.Local,
                    InScope = true,
                    Value = UnsetValue.Instance
                });
                eval.Recorder?.RecordScope(eval.CurrentFrame.Id, newSlotId, true);
            }
            else
            {
                var slotId = eval.CurrentFrame.SlotMap[local];
                var slot = eval.CurrentFrame.Slots.Find(s => s.SlotId == slotId);
                if (slot != null)
                {
                    slot.InScope = true;
                    slot.Value = UnsetValue.Instance;
                }
                eval.Recorder?.RecordScope(eval.CurrentFrame.Id, slotId, true);
            }
        }
    }
}

public class ExitBlockScopeCont : Continuation, IExceptionHandlerContinuation
{
    private readonly IBlockOperation _block;
    public ExitBlockScopeCont(IBlockOperation block) => _block = block;
    public override void Execute(Evaluator eval)
    {
        foreach (var local in _block.Locals)
        {
            if (eval.CurrentFrame.SlotMap.TryGetValue(local, out var slotId))
            {
                eval.Recorder?.RecordScope(eval.CurrentFrame.Id, slotId, false);
            }
        }
    }
}

public class ExitForEachScopeCont : Continuation, IExceptionHandlerContinuation
{
    private readonly IForEachLoopOperation _forEach;
    public ExitForEachScopeCont(IForEachLoopOperation forEach) => _forEach = forEach;
    public override void Execute(Evaluator eval)
    {
        foreach (var local in _forEach.Locals)
        {
            if (eval.CurrentFrame.SlotMap.TryGetValue(local, out var slotId))
            {
                eval.Recorder?.RecordScope(eval.CurrentFrame.Id, slotId, false);
            }
        }
    }
}

public class VarInitWriteCont : Continuation
{
    private readonly IVariableDeclaratorOperation _declarator;
    public VarInitWriteCont(IVariableDeclaratorOperation declarator) => _declarator = declarator;
    public override void Execute(Evaluator eval)
    {
        var value = eval.ValueStack.Peek(); // Var init doesn't push a result itself, but we evaluate the RHS and pop it.
        eval.ValueStack.Pop(); // Wait, discard result will pop it if we added DiscardResultCont. Actually, var init itself evaluates to the assigned value in C#? 
        // No, `int x = 5` is not an expression. The value is just pushed. 
        // We added DiscardResultCont so we don't pop it here, we let DiscardResultCont pop it, OR we pop it and push it back. Let's just pop it and push it back.
        eval.ValueStack.Push(value);
        
        if (eval.CurrentFrame.SlotMap.TryGetValue(_declarator.Symbol, out var slotId))
        {
            eval.WriteReference(new LocalRef(eval.CurrentFrame.Id, slotId), value);
        }
    }
}


/// A C# exception the traced program threw and never caught.
public class UnhandledUserException : Exception
{
    public string ExceptionName { get; }

    private UnhandledUserException(string exceptionName, string message) : base(message)
    {
        ExceptionName = exceptionName;
    }

    public static UnhandledUserException From(IValue exception, Evaluator eval)
    {
        switch (exception)
        {
            case BuiltinExceptionValue builtin:
                return new UnhandledUserException(builtin.ExceptionName, DefaultMessage(builtin.ExceptionName));

            case ObjectRef reference when eval.Heap.TryGet(reference.ObjId, out var obj) && obj is ClassObject cls:
                var name = eval.GetTypeSymbol(cls.TypeId).Name;
                var message = cls.Fields.TryGetValue("Message", out var m) && m is PrimitiveValue { Value: string text } && text.Length > 0
                    ? text
                    : DefaultMessage(name);
                return new UnhandledUserException(name, message);

            default:
                return new UnhandledUserException("Exception", "An exception was thrown.");
        }
    }

    /// The wording real .NET uses, so a search for the message finds the same answers.
    private static string DefaultMessage(string exceptionName) => exceptionName switch
    {
        "IndexOutOfRangeException" => "Index was outside the bounds of the array.",
        "NullReferenceException" => "Object reference not set to an instance of an object.",
        "DivideByZeroException" => "Attempted to divide by zero.",
        "OverflowException" => "Arithmetic operation resulted in an overflow.",
        "InvalidOperationException" => "Operation is not valid due to the current state of the object.",
        "FormatException" => "Input string was not in a correct format.",
        "KeyNotFoundException" => "The given key was not present in the dictionary.",
        "ArgumentOutOfRangeException" => "Index was out of range.",
        _ => $"Exception of type '{exceptionName}' was thrown."
    };
}


/// A resource ceiling was reached. Not an error: the partial trace is still worth showing.
public class LimitExceededException : Exception
{
    /// One of the values the trace schema allows for limitHit.
    public string Limit { get; }

    public LimitExceededException(string limit) : base($"limit_exceeded: {limit}")
    {
        Limit = limit;
    }
}
