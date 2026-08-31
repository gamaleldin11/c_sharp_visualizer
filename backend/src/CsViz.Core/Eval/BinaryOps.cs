using System;
using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;

namespace CsViz.Core.Eval;

/// Shared arithmetic/comparison/conversion core.
///
/// Extracted from BinaryCombineCont because three operations need identical semantics:
/// `a + b`, `a += b`, and `a++`. Duplicating the operator table across them is how the forms
/// drift apart - a compound assignment that overflows differently from the equivalent binary
/// expression is a bug nobody finds by reading the code.
public static class BinaryOps
{
    private static readonly ConcurrentDictionary<(Type, Type, BinaryOperatorKind, bool), Func<object, object, object>> _opCache = new();
    private static readonly ConcurrentDictionary<(Type, Type, bool), Func<object, object>> _convCache = new();

    /// Applies a binary operator. Returns false when the operands are not a shape this
    /// handles, so the caller can raise a precise diagnostic rather than pushing nothing.
    /// C#-level faults (overflow, divide by zero) set eval.UnwindingException and still
    /// return true, because user code can catch them.
    public static bool TryApply(Evaluator eval, BinaryOperatorKind kind, bool isChecked, IValue left, IValue right, out IValue result)
    {
        result = NullValue.Instance;
        bool isEq = kind == BinaryOperatorKind.Equals;
        bool isNeq = kind == BinaryOperatorKind.NotEquals;

        // Reference / null identity. Must run before the primitive path: a null operand is
        // NullValue, not a PrimitiveValue, so the arithmetic path would silently fall through.
        if (isEq || isNeq)
        {
            bool? identity = null;
            if (left is NullValue || right is NullValue)
            {
                identity = left is NullValue && right is NullValue;
            }
            else if (left is ObjectRef || right is ObjectRef)
            {
                identity = left.Equals(right);
            }

            if (identity.HasValue)
            {
                result = new PrimitiveValue(TypeCode.Boolean, isEq ? identity.Value : !identity.Value);
                return true;
            }
        }

        // C# string semantics: `==` is value equality (an operator overload), `+` is
        // concatenation. Expression.Add has no string overload and Expression.Equal on strings
        // is reference equality, so both must be handled before the LINQ-Expression path.
        if (left is PrimitiveValue { Value: string } || right is PrimitiveValue { Value: string })
        {
            if (kind == BinaryOperatorKind.Add)
            {
                result = new PrimitiveValue(TypeCode.String, Stringify(left) + Stringify(right));
                return true;
            }
            if (isEq || isNeq)
            {
                bool same = string.Equals(Stringify(left), Stringify(right), StringComparison.Ordinal);
                result = new PrimitiveValue(TypeCode.Boolean, isEq ? same : !same);
                return true;
            }
        }

        if (left is not PrimitiveValue lp || right is not PrimitiveValue rp) return false;

        var lType = lp.Value.GetType();
        var rType = rp.Value.GetType();

        // Integer division and remainder by zero throw in C#. Checking up front keeps the
        // interpreter's own exception distinguishable from a genuine host fault.
        if ((kind == BinaryOperatorKind.Divide || kind == BinaryOperatorKind.Remainder) && IsIntegral(rType))
        {
            if (Convert.ToInt64(rp.Value) == 0)
            {
                eval.UnwindingException = new BuiltinExceptionValue("DivideByZeroException");
                result = UnsetValue.Instance;
                return true;
            }
        }

        Func<object, object, object> func;
        try
        {
            func = _opCache.GetOrAdd((lType, rType, kind, isChecked), BuildOp);
        }
        catch (NotSupportedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Expression.MakeBinary rejects the operand pair, e.g. a bitwise op on doubles.
            return false;
        }

        try
        {
            var res = func(lp.Value, rp.Value);
            result = new PrimitiveValue(Type.GetTypeCode(res.GetType()), res);
            return true;
        }
        catch (OverflowException)
        {
            eval.UnwindingException = new BuiltinExceptionValue("OverflowException");
            result = UnsetValue.Instance;
            return true;
        }
        catch (DivideByZeroException)
        {
            eval.UnwindingException = new BuiltinExceptionValue("DivideByZeroException");
            result = UnsetValue.Instance;
            return true;
        }
    }

    private static bool IsIntegral(Type t) =>
        t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
        t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

    /// Renders a value the way C# string concatenation and Console.Write do.
    /// InvariantCulture is deliberate: the trace must be byte-identical on every machine, and
    /// a German-locale server would otherwise emit "3,5" where the differential test wants "3.5".
    /// bool renders as "True"/"False" because that is what bool.ToString() produces.
    public static string Stringify(IValue v) => v switch
    {
        NullValue => "",
        PrimitiveValue { Value: bool b } => b ? "True" : "False",
        PrimitiveValue p => Convert.ToString(p.Value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
        _ => ""
    };

    private static Func<object, object, object> BuildOp((Type, Type, BinaryOperatorKind, bool) key)
    {
        var (lt, rt, kind, isChecked) = key;
        var p1 = System.Linq.Expressions.Expression.Parameter(typeof(object));
        var p2 = System.Linq.Expressions.Expression.Parameter(typeof(object));
        System.Linq.Expressions.Expression leftExpr = System.Linq.Expressions.Expression.Convert(p1, lt);
        System.Linq.Expressions.Expression rightExpr = System.Linq.Expressions.Expression.Convert(p2, rt);

        // C# binary numeric promotion. Roslyn normally inserts explicit IConversionOperation
        // nodes so both operands already match, but compound assignment and ++ synthesise
        // operands directly. Shift operators deliberately keep their mismatched types.
        if (IsArithmetic(kind) || IsComparison(kind))
        {
            var promoted = Promote(lt, rt);
            if (promoted != null)
            {
                if (lt != promoted) leftExpr = System.Linq.Expressions.Expression.Convert(leftExpr, promoted);
                if (rt != promoted) rightExpr = System.Linq.Expressions.Expression.Convert(rightExpr, promoted);
            }
        }

        System.Linq.Expressions.Expression body = kind switch
        {
            BinaryOperatorKind.Add => isChecked
                ? System.Linq.Expressions.Expression.AddChecked(leftExpr, rightExpr)
                : System.Linq.Expressions.Expression.Add(leftExpr, rightExpr),
            BinaryOperatorKind.Subtract => isChecked
                ? System.Linq.Expressions.Expression.SubtractChecked(leftExpr, rightExpr)
                : System.Linq.Expressions.Expression.Subtract(leftExpr, rightExpr),
            BinaryOperatorKind.Multiply => isChecked
                ? System.Linq.Expressions.Expression.MultiplyChecked(leftExpr, rightExpr)
                : System.Linq.Expressions.Expression.Multiply(leftExpr, rightExpr),
            BinaryOperatorKind.Divide => System.Linq.Expressions.Expression.Divide(leftExpr, rightExpr),
            BinaryOperatorKind.Remainder => System.Linq.Expressions.Expression.Modulo(leftExpr, rightExpr),
            BinaryOperatorKind.Equals => System.Linq.Expressions.Expression.Equal(leftExpr, rightExpr),
            BinaryOperatorKind.NotEquals => System.Linq.Expressions.Expression.NotEqual(leftExpr, rightExpr),
            BinaryOperatorKind.LessThan => System.Linq.Expressions.Expression.LessThan(leftExpr, rightExpr),
            BinaryOperatorKind.LessThanOrEqual => System.Linq.Expressions.Expression.LessThanOrEqual(leftExpr, rightExpr),
            BinaryOperatorKind.GreaterThan => System.Linq.Expressions.Expression.GreaterThan(leftExpr, rightExpr),
            BinaryOperatorKind.GreaterThanOrEqual => System.Linq.Expressions.Expression.GreaterThanOrEqual(leftExpr, rightExpr),
            BinaryOperatorKind.And => System.Linq.Expressions.Expression.And(leftExpr, rightExpr),
            BinaryOperatorKind.Or => System.Linq.Expressions.Expression.Or(leftExpr, rightExpr),
            BinaryOperatorKind.ExclusiveOr => System.Linq.Expressions.Expression.ExclusiveOr(leftExpr, rightExpr),
            BinaryOperatorKind.LeftShift => System.Linq.Expressions.Expression.LeftShift(leftExpr, MaskShift(rightExpr, lt)),
            BinaryOperatorKind.RightShift => System.Linq.Expressions.Expression.RightShift(leftExpr, MaskShift(rightExpr, lt)),
            _ => throw new NotSupportedException($"Binary operator {kind} is not supported.")
        };

        var castBody = System.Linq.Expressions.Expression.Convert(body, typeof(object));
        return System.Linq.Expressions.Expression.Lambda<Func<object, object, object>>(castBody, p1, p2).Compile();
    }

    /// C# masks the shift count to the operand width rather than shifting past the type's
    /// bit count, so `1 << 32` is 1, not 0.
    private static System.Linq.Expressions.Expression MaskShift(System.Linq.Expressions.Expression right, Type leftType)
    {
        int mask = leftType == typeof(long) || leftType == typeof(ulong) ? 63 : 31;
        return System.Linq.Expressions.Expression.And(
            System.Linq.Expressions.Expression.Convert(right, typeof(int)),
            System.Linq.Expressions.Expression.Constant(mask));
    }

    private static bool IsArithmetic(BinaryOperatorKind k) =>
        k is BinaryOperatorKind.Add or BinaryOperatorKind.Subtract or BinaryOperatorKind.Multiply
          or BinaryOperatorKind.Divide or BinaryOperatorKind.Remainder
          or BinaryOperatorKind.And or BinaryOperatorKind.Or or BinaryOperatorKind.ExclusiveOr;

    private static bool IsComparison(BinaryOperatorKind k) =>
        k is BinaryOperatorKind.Equals or BinaryOperatorKind.NotEquals or BinaryOperatorKind.LessThan
          or BinaryOperatorKind.LessThanOrEqual or BinaryOperatorKind.GreaterThan
          or BinaryOperatorKind.GreaterThanOrEqual;

    /// C# binary numeric promotion, in the order the language spec tests it. Anything narrower
    /// than int promotes to int, which is why `(byte)200 + (byte)100` is 300 rather than
    /// wrapping to 44.
    private static Type? Promote(Type l, Type r)
    {
        if (l == typeof(decimal) || r == typeof(decimal)) return typeof(decimal);
        if (l == typeof(double) || r == typeof(double)) return typeof(double);
        if (l == typeof(float) || r == typeof(float)) return typeof(float);
        if (l == typeof(ulong) || r == typeof(ulong)) return typeof(ulong);
        if (l == typeof(long) || r == typeof(long)) return typeof(long);
        if (l == typeof(uint) && IsSignedSmall(r)) return typeof(long);
        if (r == typeof(uint) && IsSignedSmall(l)) return typeof(long);
        if (l == typeof(uint) || r == typeof(uint)) return typeof(uint);
        if (IsNumeric(l) && IsNumeric(r)) return typeof(int);
        return null;
    }

    private static bool IsSignedSmall(Type t) => t == typeof(sbyte) || t == typeof(short) || t == typeof(int);

    private static bool IsNumeric(Type t) =>
        IsIntegral(t) || t == typeof(char) || t == typeof(float) || t == typeof(double) || t == typeof(decimal);

    /// A C# cast, with the language's exact truncation rules. Expression.Convert compiles to
    /// the CLR conv instruction, so `(int)3.9` is 3 by truncation - not 4, which is what
    /// Convert.ToInt32 would give and is a classic way an interpreter drifts from real C#.
    public static bool TryConvert(Evaluator eval, IValue value, SpecialType target, bool isChecked, out IValue result)
    {
        result = value;
        var clrTarget = ClrTypeFor(target);
        if (clrTarget == null || value is not PrimitiveValue pv) return true;
        if (pv.Value.GetType() == clrTarget) return true;

        Func<object, object> func;
        try
        {
            func = _convCache.GetOrAdd((pv.Value.GetType(), clrTarget, isChecked), key =>
            {
                var p = System.Linq.Expressions.Expression.Parameter(typeof(object));
                var operand = System.Linq.Expressions.Expression.Convert(p, key.Item1);
                System.Linq.Expressions.Expression conv = key.Item3
                    ? System.Linq.Expressions.Expression.ConvertChecked(operand, key.Item2)
                    : System.Linq.Expressions.Expression.Convert(operand, key.Item2);
                return System.Linq.Expressions.Expression.Lambda<Func<object, object>>(
                    System.Linq.Expressions.Expression.Convert(conv, typeof(object)), p).Compile();
            });
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        try
        {
            var converted = func(pv.Value);
            result = new PrimitiveValue(Type.GetTypeCode(converted.GetType()), converted);
            return true;
        }
        catch (OverflowException)
        {
            eval.UnwindingException = new BuiltinExceptionValue("OverflowException");
            result = UnsetValue.Instance;
            return true;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }

    public static Type? ClrTypeFor(SpecialType t) => t switch
    {
        SpecialType.System_Boolean => typeof(bool),
        SpecialType.System_Char => typeof(char),
        SpecialType.System_SByte => typeof(sbyte),
        SpecialType.System_Byte => typeof(byte),
        SpecialType.System_Int16 => typeof(short),
        SpecialType.System_UInt16 => typeof(ushort),
        SpecialType.System_Int32 => typeof(int),
        SpecialType.System_UInt32 => typeof(uint),
        SpecialType.System_Int64 => typeof(long),
        SpecialType.System_UInt64 => typeof(ulong),
        SpecialType.System_Single => typeof(float),
        SpecialType.System_Double => typeof(double),
        SpecialType.System_Decimal => typeof(decimal),
        _ => null
    };

    /// The value C# gives an unassigned field or array element of this type.
    public static IValue DefaultFor(ITypeSymbol? type)
    {
        if (type == null) return NullValue.Instance;
        if (type.TypeKind == TypeKind.Enum) return new PrimitiveValue(TypeCode.Int32, 0);
        return type.SpecialType switch
        {
            SpecialType.System_Int32 => new PrimitiveValue(TypeCode.Int32, 0),
            SpecialType.System_Int64 => new PrimitiveValue(TypeCode.Int64, 0L),
            SpecialType.System_Int16 => new PrimitiveValue(TypeCode.Int16, (short)0),
            SpecialType.System_UInt32 => new PrimitiveValue(TypeCode.UInt32, 0u),
            SpecialType.System_UInt64 => new PrimitiveValue(TypeCode.UInt64, 0ul),
            SpecialType.System_UInt16 => new PrimitiveValue(TypeCode.UInt16, (ushort)0),
            SpecialType.System_Byte => new PrimitiveValue(TypeCode.Byte, (byte)0),
            SpecialType.System_SByte => new PrimitiveValue(TypeCode.SByte, (sbyte)0),
            SpecialType.System_Double => new PrimitiveValue(TypeCode.Double, 0d),
            SpecialType.System_Single => new PrimitiveValue(TypeCode.Single, 0f),
            SpecialType.System_Decimal => new PrimitiveValue(TypeCode.Decimal, 0m),
            SpecialType.System_Boolean => new PrimitiveValue(TypeCode.Boolean, false),
            // '\0', not ' '. A space is a different character and shows up wrong in output.
            SpecialType.System_Char => new PrimitiveValue(TypeCode.Char, '\0'),
            _ => NullValue.Instance
        };
    }
}
