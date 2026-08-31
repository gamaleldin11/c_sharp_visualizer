using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CsViz.Core.Values;

public interface IValue { }

public sealed record PrimitiveValue(TypeCode TypeCode, object Value) : IValue;

public sealed record NullValue : IValue
{
    public static readonly NullValue Instance = new();
}

public sealed record BuiltinExceptionValue(string ExceptionName) : IValue;

public sealed record ObjectRef(int ObjId) : IValue;

public sealed record StructValue(ITypeSymbol Type, ImmutableDictionary<string, IValue> Fields) : IValue;

public sealed record UnsetValue : IValue
{
    public static readonly UnsetValue Instance = new();
}
