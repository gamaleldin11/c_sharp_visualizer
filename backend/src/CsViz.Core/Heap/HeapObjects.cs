using System.Collections.Generic;
using CsViz.Core.Values;

namespace CsViz.Core.Heap;

public abstract record InterpObject(int TypeId);

public sealed record ClassObject(int TypeId, Dictionary<string, IValue> Fields) : InterpObject(TypeId);

public sealed record ArrayObject(int TypeId, int[] Dims, IValue[] Elems) : InterpObject(TypeId);

public sealed record ListObject(int TypeId, int Count, int Capacity, IValue[] Backing) : InterpObject(TypeId);

public sealed record DictObject(int TypeId, List<KeyValuePair<IValue, IValue>> Entries) : InterpObject(TypeId);

public sealed record StackObject(int TypeId, List<IValue> Items) : InterpObject(TypeId);

public sealed record QueueObject(int TypeId, List<IValue> Items) : InterpObject(TypeId);

public sealed record BoxedObject(int TypeId, IValue Value) : InterpObject(TypeId);
