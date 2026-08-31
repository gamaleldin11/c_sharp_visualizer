using System.Collections.Generic;

namespace CsViz.Core.Heap;

public class InterpHeap
{
    private readonly Dictionary<int, InterpObject> _objects = new();
    private int _nextId = 1;

    public int Allocate(InterpObject obj)
    {
        int id = _nextId++;
        _objects[id] = obj;
        return id;
    }

    /// Number of live objects, used to enforce the heap ceiling.
    public int Count => _objects.Count;

    public InterpObject Get(int id) => _objects[id];
    
    public bool TryGet(int id, out InterpObject? obj) => _objects.TryGetValue(id, out obj);

    public void Set(int id, InterpObject obj) => _objects[id] = obj;
    
    public IEnumerable<KeyValuePair<int, InterpObject>> GetAll() => _objects;
}
