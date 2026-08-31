using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using CsViz.Core.Values;

namespace CsViz.Core.Frames;

public enum SlotKind { Local, Param }

public class Slot
{
    public int SlotId { get; init; }
    public string Name { get; init; } = "";
    public SlotKind Kind { get; init; }
    public int DeclaredLine { get; init; }
    public bool InScope { get; set; }
    public IValue Value { get; set; } = UnsetValue.Instance;
}

public class Frame
{
    public int Id { get; init; }
    public IMethodSymbol Method { get; init; } = null!;
    public Dictionary<ISymbol, int> SlotMap { get; init; } = new(SymbolEqualityComparer.Default);
    public List<Slot> Slots { get; init; } = new();
    public IValue ThisValue { get; set; } = NullValue.Instance;
    public Dictionary<ITypeParameterSymbol, ITypeSymbol> TypeArgs { get; init; } = new(SymbolEqualityComparer.Default);

    /// For `ref` and `out` parameters: which caller storage location each one writes back to
    /// when the frame is popped. Null when the method has none, which is almost always.
    public IReadOnlyList<(int Index, IReference Target)>? RefBindings { get; set; }
}
