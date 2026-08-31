using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CsViz.Core.Eval;

public interface IMethodProvider
{
    IOperation? GetMethodBody(IMethodSymbol method);

    /// The `: base(...)` / `: this(...)` clause of a constructor, if it has one.
    IOperation? GetConstructorInitializer(IMethodSymbol constructor) => null;

    /// Field initializers declared on a type, in source order, e.g. `public int V = 5;`.
    IReadOnlyList<IFieldInitializerOperation> GetFieldInitializers(INamedTypeSymbol type) =>
        System.Array.Empty<IFieldInitializerOperation>();
}
