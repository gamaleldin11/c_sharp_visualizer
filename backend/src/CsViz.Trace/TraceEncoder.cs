using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core;
using CsViz.Core.Values;
using CsViz.Core.Eval;
using CsViz.Core.Recorder;
using CsViz.Core.Heap;
using CsViz.Core.Frames;

namespace CsViz.Trace;

public class TraceEncoder : ITraceRecorder
{
    private readonly Evaluator _eval;
    
    private readonly Dictionary<string, int> _stringPool = new();
    private readonly List<string> _strings = new();
    
    private readonly Dictionary<ITypeSymbol, int> _typePool = new(SymbolEqualityComparer.Default);
    private readonly List<TypeInfoDto> _types = new();
    
    private readonly List<SnapshotDto> _snapshots = new();
    private readonly List<KeyframeDto> _keyframes = new();
    private readonly List<StepDto> _steps = new();
    
    private List<object[]> _currentDelta = new();
    private StepEventDto? _currentEvent;

    // Statements nest (a call sits inside an expression statement, and the callee's own
    // statements open steps while the caller's step is still open). A single "current step"
    // field loses the outer step's ops - including pushFrame - when the inner BeginStep
    // replaces it. Track open steps as a stack and share one delta buffer, flushed per step.
    private readonly Stack<(IOperation Op, string Kind)> _openSteps = new();
    private readonly System.Text.StringBuilder _stdout = new();

    public List<StepDto> Steps => _steps;

    /// Everything the program printed, for the differential oracle to compare against real .NET.
    public string Stdout => _stdout.ToString();
    
    public TraceEncoder(Evaluator eval)
    {
        _eval = eval;
    }
    
    public TraceDto BuildTrace(string sourceHash, string source, string status, string? limitHit = null, List<DiagnosticDto>? diagnostics = null, string? stdin = null, List<MethodAnalysisDto>? methods = null)
    {
        return new TraceDto(
            Version: 1,
            SourceHash: sourceHash,
            Source: source,
            Stdin: stdin,
            Status: status,
            LimitHit: limitHit,
            Diagnostics: diagnostics ?? new List<DiagnosticDto>(),
            Strings: _strings,
            Types: _types,
            Snapshots: _snapshots,
            Keyframes: _keyframes,
            Steps: _steps,
            Methods: methods ?? new List<MethodAnalysisDto>()
        );
    }
    
    private int GetStringId(string s)
    {
        if (_stringPool.TryGetValue(s, out var id)) return id;
        id = _strings.Count;
        _strings.Add(s);
        _stringPool[s] = id;
        return id;
    }
    
    private int GetTypeId(ITypeSymbol? t)
    {
        if (t == null) return -1;
        if (_typePool.TryGetValue(t, out var id)) return id;
        id = _types.Count;
        
        string kind = "class";
        if (t.IsValueType)
        {
            kind = t.TypeKind == TypeKind.Enum ? "enum" : "struct";
        }
        else if (t.TypeKind == TypeKind.Array)
        {
            kind = "array";
        }
        else if (t.TypeKind == TypeKind.Interface)
        {
            kind = "interface";
        }
        
        _typePool[t] = id;
        _types.Add(new TypeInfoDto(id, t.ToDisplayString(), kind));
        return id;
    }
    
    // The wire format uses C# keyword names, not .NET TypeCode names ("int", not "Int32"),
    // so the frontend never has to know about the CLR's type naming.
    private static string PrimTypeName(TypeCode code) => code switch
    {
        TypeCode.Int32 => "int",
        TypeCode.Int64 => "long",
        TypeCode.Int16 => "short",
        TypeCode.Byte => "byte",
        TypeCode.SByte => "sbyte",
        TypeCode.UInt16 => "ushort",
        TypeCode.UInt32 => "uint",
        TypeCode.UInt64 => "ulong",
        TypeCode.Double => "double",
        TypeCode.Single => "float",
        TypeCode.Decimal => "decimal",
        TypeCode.Boolean => "bool",
        TypeCode.Char => "char",
        TypeCode.String => "string",
        _ => code.ToString().ToLowerInvariant()
    };

    private ValueDto MapValue(IValue value)
    {
        return value switch
        {
            NullValue => new NullValueDto(),
            UnsetValue => new UnsetValueDto(),
            ObjectRef o => new RefValueDto(o.ObjId),
            PrimitiveValue p => new PrimValueDto(PrimTypeName(p.TypeCode), p.Value),
            StructValue s => new StructValueDto(GetTypeId(s.Type), OrderStructFields(s)),
            _ => new UnsetValueDto()
        };
    }
    
    /// Struct fields in the order they were declared.
    ///
    /// StructValue holds its fields in an ImmutableDictionary, which enumerates in hash order.
    /// .NET randomises string hashing per process, so serialising that directly made the trace
    /// non-deterministic: the same program produced different JSON on consecutive runs, which
    /// breaks the source-hash cache and any byte comparison of two traces.
    ///
    /// Declaration order is also what a reader expects to see - a Point should render X then Y,
    /// not whichever the hash happened to put first.
    private Dictionary<string, ValueDto> OrderStructFields(StructValue s)
    {
        var ordered = new Dictionary<string, ValueDto>();

        foreach (var member in s.Type.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsStatic || member.IsImplicitlyDeclared) continue;
            if (s.Fields.TryGetValue(member.Name, out var value))
            {
                ordered[member.Name] = MapValue(value);
            }
        }

        // Synthetic fields with no declaring symbol - KeyValuePair's Key and Value, which the
        // dictionary shim builds by hand. Sorted so they are at least stable.
        foreach (var kvp in s.Fields.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!ordered.ContainsKey(kvp.Key))
            {
                ordered[kvp.Key] = MapValue(kvp.Value);
            }
        }

        return ordered;
    }

    private HeapObjectDto MapHeapObj(InterpObject obj)
    {
        var sym = _eval.GetTypeSymbol(obj.TypeId);
        return obj switch
        {
            ClassObject cls => new ObjectHeapDto(GetTypeId(sym), cls.Fields.Select(f => new ObjectFieldDto(f.Key, MapValue(f.Value))).ToList()),
            ArrayObject arr => new ArrayHeapDto(GetTypeId(sym), arr.Dims.ToList(), arr.Elems.Select(MapValue).ToList()),
            ListObject lst => new ListHeapDto(GetTypeId(sym), lst.Count, lst.Capacity, lst.Backing.Select(MapValue).ToList()),
            DictObject dct => new DictHeapDto(GetTypeId(sym), dct.Entries.Select(e => new DictEntryDto(MapValue(e.Key), MapValue(e.Value))).ToList()),
            BoxedObject bx => new BoxedHeapDto(GetTypeId(sym), MapValue(bx.Value)),
            StackObject st => new SeqHeapDto(GetTypeId(sym), "stack", st.Items.Select(MapValue).ToList()),
            QueueObject q => new SeqHeapDto(GetTypeId(sym), "queue", q.Items.Select(MapValue).ToList()),
            _ => throw new NotSupportedException($"No wire representation for {obj.GetType().Name}.")
        };
    }
    
    private FrameDto MapFrame(Frame f)
    {
        var slots = f.Slots.Select(s => new SlotDto(
            s.SlotId,
            s.Name,
            s.Kind == SlotKind.Local ? "local" : "param",
            s.DeclaredLine,
            s.InScope,
            MapValue(s.Value)
        )).ToList();
        
        return new FrameDto(f.Id, f.Method.Name, f.Method.ContainingType?.Name ?? "", 0, slots);
    }
    
    private SnapshotDto CaptureSnapshot()
    {
        var frames = _eval.FrameStack.Reverse().Select(MapFrame).ToList();
        var heap = _eval.Heap.GetAll().ToDictionary(kvp => kvp.Key.ToString(), kvp => MapHeapObj(kvp.Value));
        // Cumulative stdout must be part of the keyframe, otherwise seeking the scrubber to a
        // keyframe silently clears the output pane.
        return new SnapshotDto(frames, heap, _stdout.ToString());
    }
    
    // ---------------------------------------------------------------------------------
    // Step boundaries
    //
    // Statements nest: a call sits inside an expression statement, and the callee's own
    // statements open steps while the caller's is still open. A step is therefore emitted when
    // execution ARRIVES somewhere new, not when a statement finishes - the ops recorded since
    // the last boundary belong to the statement that was running, which is the innermost one
    // that had been opened.
    //
    // The earlier design closed steps on EndStep and shared one delta buffer. Because the
    // innermost statement closes first, it collected every op the outer statement had
    // recorded, including its pushFrame. In a recursive program that put six frame pushes on
    // one step and labelled the entire descent with the callee's first line - so the editor
    // highlight, the flowchart heat map, and the dataflow view all pointed at the wrong line.
    // ---------------------------------------------------------------------------------

    /// The step currently accumulating ops. Null before the first statement.
    private (IOperation Op, string Kind, bool DropIfEmpty)? _pending;

    public void BeginStep(IOperation op, string kind)
    {
        // Arriving at a new statement closes whatever was running.
        FlushPending();
        _openSteps.Push((op, kind));
        _pending = (op, kind, DropIfEmpty: false);
    }

    public void EndStep()
    {
        if (_openSteps.Count == 0) return;

        FlushPending();
        _openSteps.Pop();

        // Returning to the enclosing statement is itself a position worth showing - it is what
        // puts the highlight back on the call line after a call returns. It is dropped if
        // nothing further happens there, so a statement whose last act was the call does not
        // emit a duplicate empty step.
        if (_openSteps.Count > 0)
        {
            var parent = _openSteps.Peek();
            _pending = (parent.Op, parent.Kind, DropIfEmpty: true);
        }
        else
        {
            _pending = null;
        }
    }

    private void FlushPending()
    {
        if (_pending is not { } pending) return;
        _pending = null;

        if (pending.DropIfEmpty && _currentDelta.Count == 0 && _currentEvent == null)
        {
            return;
        }

        var pos = pending.Op.Syntax.GetLocation().GetLineSpan();

        int stepIdx = _steps.Count;
        if (stepIdx % KeyframeInterval == 0)
        {
            // Captured here, after this step's ops have already been applied to the evaluator.
            // TracePlayer relies on that: stateAt(n) = snapshot(K) + deltas of steps K+1..n.
            _snapshots.Add(CaptureSnapshot());
            _keyframes.Add(new KeyframeDto(stepIdx, _snapshots.Count - 1));
        }

        if (_steps.Count >= MaxSteps)
        {
            // Enforced here rather than in the evaluator because this is a property of the
            // trace, not of the work: the browser has to hold every one of these.
            throw new CsViz.Core.Eval.LimitExceededException("steps");
        }

        _steps.Add(new StepDto(
            stepIdx,
            pos.StartLinePosition.Line + 1,
            pos.StartLinePosition.Character + 1,
            pos.EndLinePosition.Line + 1,
            pos.EndLinePosition.Character + 1,
            pending.Kind,
            _eval.FrameStack.Count,
            _currentDelta,
            _currentEvent));

        _currentDelta = new();
        _currentEvent = null;
    }

    /// Emits any step still open when the program ends, so the final statement is not lost.
    public void Finish()
    {
        while (_openSteps.Count > 0)
        {
            FlushPending();
            _openSteps.Pop();
            if (_openSteps.Count > 0)
            {
                var parent = _openSteps.Peek();
                _pending = (parent.Op, parent.Kind, DropIfEmpty: true);
            }
        }
        FlushPending();
    }

    private const int KeyframeInterval = 256;

    /// Ceiling on recorded steps - the number the user actually scrubs through, and the one the
    /// plan's 15k budget refers to. Bounds the trace's size on the wire and in the browser.
    public int MaxSteps { get; set; } = 15_000;
    
    public void RecordScope(int frameId, int slotId, bool inScope)
    {
        _currentDelta.Add(new object[] { "scope", frameId, slotId, inScope });
    }
    
    public void RecordSetLocal(int frameId, int slotId, IValue value)
    {
        _currentDelta.Add(new object[] { "setLocal", frameId, slotId, MapValue(value) });
    }
    
    public void RecordSetField(int objId, string fieldName, IValue value)
    {
        _currentDelta.Add(new object[] { "setField", objId, fieldName, MapValue(value) });
    }
    
    public void RecordSetElem(int objId, int index, IValue value)
    {
        _currentDelta.Add(new object[] { "setElem", objId, index, MapValue(value) });
    }
    
    public void RecordNewObj(int objId, InterpObject obj)
    {
        _currentDelta.Add(new object[] { "newObj", objId, MapHeapObj(obj) });
    }
    
    public void RecordPushFrame(Frame frame)
    {
        _currentDelta.Add(new object[] { "pushFrame", MapFrame(frame) });
    }
    
    public void RecordPopFrame()
    {
        _currentDelta.Add(new object[] { "popFrame" });
    }
    
    public void RecordStdout(string text)
    {
        _stdout.Append(text);
        // Reported back to the evaluator so the output ceiling is enforced in the same place
        // as every other limit, rather than growing unbounded here.
        _eval.OutputChars = _stdout.Length;
        _currentDelta.Add(new object[] { "stdout", GetStringId(text) });
    }
}
