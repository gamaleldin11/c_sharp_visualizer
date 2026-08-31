using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CsViz.Trace;

public record TraceDto(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("sourceHash")] string SourceHash,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("stdin")] string? Stdin,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("limitHit")] string? LimitHit,
    [property: JsonPropertyName("diagnostics")] List<DiagnosticDto> Diagnostics,
    [property: JsonPropertyName("strings")] List<string> Strings,
    [property: JsonPropertyName("types")] List<TypeInfoDto> Types,
    [property: JsonPropertyName("snapshots")] List<SnapshotDto> Snapshots,
    [property: JsonPropertyName("keyframes")] List<KeyframeDto> Keyframes,
    [property: JsonPropertyName("steps")] List<StepDto> Steps,
    [property: JsonPropertyName("methods")] List<MethodAnalysisDto> Methods
);

public record DiagnosticDto(
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("col")] int Col,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("endCol")] int EndCol,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("id")] string Id
);

public record TypeInfoDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind
);

public record KeyframeDto(
    [property: JsonPropertyName("stepIndex")] int StepIndex,
    [property: JsonPropertyName("snapshotIndex")] int SnapshotIndex
);

public record SnapshotDto(
    [property: JsonPropertyName("frames")] List<FrameDto> Frames,
    [property: JsonPropertyName("heap")] Dictionary<string, HeapObjectDto> Heap,
    [property: JsonPropertyName("stdout")] string Stdout
);

public record FrameDto(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("methodName")] string MethodName,
    [property: JsonPropertyName("declaringType")] string DeclaringType,
    [property: JsonPropertyName("callLine")] int CallLine,
    [property: JsonPropertyName("slots")] List<SlotDto> Slots
);

public record SlotDto(
    [property: JsonPropertyName("slotId")] int SlotId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("declaredLine")] int DeclaredLine,
    [property: JsonPropertyName("inScope")] bool InScope,
    [property: JsonPropertyName("value")] ValueDto Value
);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(PrimValueDto), "prim")]
[JsonDerivedType(typeof(NullValueDto), "null")]
[JsonDerivedType(typeof(RefValueDto), "ref")]
[JsonDerivedType(typeof(StructValueDto), "struct")]
[JsonDerivedType(typeof(UnsetValueDto), "unset")]
public abstract record ValueDto;

public record PrimValueDto(
    [property: JsonPropertyName("t")] string T,
    [property: JsonPropertyName("v")] object? V
) : ValueDto;

public record NullValueDto() : ValueDto;

public record RefValueDto(
    [property: JsonPropertyName("id")] int Id
) : ValueDto;

public record StructValueDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("fields")] Dictionary<string, ValueDto> Fields
) : ValueDto;

public record UnsetValueDto() : ValueDto;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(ObjectHeapDto), "object")]
[JsonDerivedType(typeof(ArrayHeapDto), "array")]
[JsonDerivedType(typeof(ListHeapDto), "list")]
[JsonDerivedType(typeof(DictHeapDto), "dict")]
[JsonDerivedType(typeof(BoxedHeapDto), "boxed")]
[JsonDerivedType(typeof(SeqHeapDto), "seq")]
public abstract record HeapObjectDto;

public record ObjectFieldDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] ValueDto Value
);

public record ObjectHeapDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("fields")] List<ObjectFieldDto> Fields
) : HeapObjectDto;

public record ArrayHeapDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("dims")] List<int> Dims,
    [property: JsonPropertyName("elems")] List<ValueDto> Elems
) : HeapObjectDto;

public record ListHeapDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("capacity")] int Capacity,
    [property: JsonPropertyName("backing")] List<ValueDto> Backing
) : HeapObjectDto;

public record DictEntryDto(
    [property: JsonPropertyName("key")] ValueDto Key,
    [property: JsonPropertyName("value")] ValueDto Value
);

public record DictHeapDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("entries")] List<DictEntryDto> Entries
) : HeapObjectDto;

public record BoxedHeapDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("value")] ValueDto Value
) : HeapObjectDto;

/// Stack<T> and Queue<T>. Both are ordered sequences whose only visual difference is which
/// end is "next", so they share one shape and carry the end as a field rather than needing
/// two near-identical node renderers.
public record SeqHeapDto(
    [property: JsonPropertyName("t")] int T,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("items")] List<ValueDto> Items
) : HeapObjectDto;

public record StepDto(
    [property: JsonPropertyName("i")] int I,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("col")] int Col,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("endCol")] int EndCol,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("frameDepth")] int FrameDepth,
    [property: JsonPropertyName("delta")] List<object[]> Delta,
    [property: JsonPropertyName("event")] StepEventDto? Event = null
);

public record StepEventDto(
    [property: JsonPropertyName("callee")] string? Callee = null,
    [property: JsonPropertyName("returnValue")] ValueDto? ReturnValue = null,
    [property: JsonPropertyName("exception")] ExceptionEventDto? Exception = null
);

public record ExceptionEventDto(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message
);

/// One basic block of a method's control-flow graph, for the flowchart view.
public record CfgBlockDto(
    [property: JsonPropertyName("ordinal")] int Ordinal,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("lines")] List<int> Lines,
    [property: JsonPropertyName("condition")] string? Condition,
    [property: JsonPropertyName("fallThrough")] int? FallThrough,
    [property: JsonPropertyName("conditionalTarget")] int? ConditionalTarget,
    [property: JsonPropertyName("conditionalLabel")] string? ConditionalLabel,
    [property: JsonPropertyName("reachable")] bool Reachable
);

/// Variables a source line reads and writes, determined statically.
public record LineFactsDto(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("reads")] List<string> Reads,
    [property: JsonPropertyName("writes")] List<string> Writes
);

/// Everything the static views need about one method.
///
/// Shipped with the trace rather than fetched separately: the flowchart is only meaningful
/// alongside the execution counts that come from the steps, and two round trips would let the
/// two drift out of sync.
public record MethodAnalysisDto(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("declaringType")] string DeclaringType,
    [property: JsonPropertyName("startLine")] int StartLine,
    [property: JsonPropertyName("endLine")] int EndLine,
    [property: JsonPropertyName("blocks")] List<CfgBlockDto> Blocks,
    [property: JsonPropertyName("lineFacts")] List<LineFactsDto> LineFacts
);
