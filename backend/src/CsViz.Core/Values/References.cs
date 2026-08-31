namespace CsViz.Core.Values;

public interface IReference { }

public sealed record LocalRef(int FrameId, int SlotId) : IReference;

public sealed record FieldRef(int ObjId, string Name) : IReference;

public sealed record ArrayElemRef(int ObjId, int[] Indices) : IReference;

public sealed record StaticFieldRef(int TypeId, string Name) : IReference;

public sealed record StructFieldRef(IReference Parent, string FieldName) : IReference;

/// A property or indexer used as an assignment target, e.g. `list[3] = 99` or `p.X = 1`.
///
/// The instance and arguments are captured at the point the target is evaluated, not when the
/// write happens. C# guarantees the target expression is evaluated once, so `list[Next()] += 1`
/// must not call Next() twice - keeping the evaluated operands in the reference is what
/// enforces that.
public sealed record PropertyRef(
    Microsoft.CodeAnalysis.IPropertySymbol Property,
    IValue Instance,
    IValue[] Arguments) : IReference;

/// The current frame's `this`, used when a struct method mutates its own receiver.
public sealed record ThisRef(int FrameId) : IReference;

/// The object being built by an object initializer, addressed by its depth on the evaluator's
/// implicit-receiver stack. Nested initializers are why this is a stack and not one slot.
public sealed record ReceiverRef(int Index) : IReference;
