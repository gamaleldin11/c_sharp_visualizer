using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Values;
using CsViz.Core.Heap;

namespace CsViz.Core.Eval;

public class ObjectCreationCont : Continuation
{
    private readonly IObjectCreationOperation _op;
    public ObjectCreationCont(IObjectCreationOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        var type = _op.Type;
        if (type == null) throw new Exception("ObjectCreationOperation has no type");

        // 1. Allocate the object
        IValue newObjValue;
        if (type.IsReferenceType)
        {
            // Class Object
            var fields = new Dictionary<string, IValue>();
            if (Bridge.BCLBridge.IsBclException(type))
            {
                // A BCL exception's real fields are private implementation detail
                // (_message, _stackTrace, _HResult...). Showing them would be noise; the
                // constructor shim fills Message in.
                fields["Message"] = new PrimitiveValue(TypeCode.String, "");
            }
            else
            {
                foreach (var member in InheritedFields(type))
                {
                    fields[member.Name] = DefaultValue(member.Type);
                }
            }

            var typeId = eval.GetTypeId(type);
            var obj = new ClassObject(typeId, fields);
            var objId = eval.Heap.Allocate(obj);
            eval.Recorder?.RecordNewObj(objId, obj);
            
            newObjValue = new ObjectRef(objId);
        }
        else
        {
            // Struct Value
            var fields = System.Collections.Immutable.ImmutableDictionary.CreateBuilder<string, IValue>();
            foreach (var member in InheritedFields(type))
            {
                fields[member.Name] = DefaultValue(member.Type);
            }
            newObjValue = new StructValue(type, fields.ToImmutable());
        }

        // 2. Call constructor if there is one
        if (_op.Constructor != null)
        {
            // Arguments were pushed left to right, so they pop last-to-first: fill backwards.
            // Filling forwards reversed them, and MethodCallCont's own reverse pop cancelled
            // the error for one argument but not for two - `new Point(1, 2)` bound X to 2.
            var args = new IValue[_op.Arguments.Length];
            for (int i = args.Length - 1; i >= 0; i--)
            {
                args[i] = eval.ValueStack.Pop();
            }

            // A class is a reference, so the object the constructor mutates is the same one the
            // caller gets: push it now and discard the constructor's own null return.
            //
            // A struct is a value. Its constructor rebuilds `this` on every field write (struct
            // values are immutable records here), so the copy pushed before the call would be
            // the empty one - `new Point(1, 2)` yielded X = 0. For structs the constructor
            // itself yields the finished value, via PopFrameCont.
            bool isStruct = !type.IsReferenceType;

            if (!isStruct)
            {
                eval.ValueStack.Push(newObjValue); // Result, left for the caller
            }

            eval.ValueStack.Push(newObjValue); // Instance for the constructor
            for (int i = 0; i < args.Length; i++)
            {
                eval.ValueStack.Push(args[i]);
            }

            ScheduleInitializer(eval, _op.Initializer);

            if (!isStruct)
            {
                eval.ContStack.Push(new DiscardResultCont());
            }
            eval.ContStack.Push(new MethodCallCont(_op.Constructor, hasInstance: true, argCount: _op.Arguments.Length));
        }
        else
        {
            eval.ValueStack.Push(newObjValue);
            ScheduleInitializer(eval, _op.Initializer);
        }
    }

    /// Runs an object or collection initializer: `new Point { X = 1 }`, `new List&lt;int&gt; { 1, 2 }`.
    ///
    /// The member assignments inside refer to the new object through an implicit receiver, and
    /// there is no frame whose `this` it could come from. The object is therefore pushed onto
    /// the evaluator's implicit-receiver stack for the duration, which lets the ordinary
    /// assignment and invocation paths handle the contents unchanged.
    ///
    /// Silently skipping this is what made `new MyStruct { X = 1 }` produce X = 0.
    internal static void ScheduleInitializer(Evaluator eval, IObjectOrCollectionInitializerOperation? initializer)
    {
        if (initializer == null || initializer.Initializers.Length == 0) return;

        // Pushed bottom-up: pop the receiver last, after every member has been assigned.
        eval.ContStack.Push(new PopReceiverCont());

        for (int i = initializer.Initializers.Length - 1; i >= 0; i--)
        {
            // Each member assignment (or collection Add call) yields a value that nothing
            // consumes.
            eval.ContStack.Push(new DiscardResultCont());
            eval.ContStack.Push(new EvalOperationCont(initializer.Initializers[i]));
        }

        eval.ContStack.Push(new PushReceiverCont());
    }

    // Every value type used to default to int 0, so a `bool` field rendered as 0 and a
    // `double` field as an int. BinaryOps.DefaultFor is the single definition of C# defaults,
    // shared with array initialisation.
    private IValue DefaultValue(ITypeSymbol type) => BinaryOps.DefaultFor(type);

    /// Instance fields declared by the type and by every base type.
    ///
    /// ITypeSymbol.GetMembers() returns only what the type itself declares, so a Dog created
    /// from `class Dog : Animal` had no Name field at all and reading it produced Unset.
    /// Walking base-first means a derived type that redeclares a name wins, matching how the
    /// runtime layout resolves it for our purposes.
    internal static IEnumerable<IFieldSymbol> InheritedFields(ITypeSymbol type)
    {
        var chain = new List<ITypeSymbol>();
        for (var current = type; current != null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            chain.Add(current);
        }
        chain.Reverse();

        foreach (var level in chain)
        {
            foreach (var field in level.GetMembers().OfType<IFieldSymbol>())
            {
                // Backing fields of auto-properties are compiler-generated and named
                // "<Prop>k__BackingField"; the property itself is what the user wrote.
                if (field.IsStatic || field.IsImplicitlyDeclared) continue;
                yield return field;
            }
        }
    }
}

/// Moves the just-created object from the value stack onto the implicit-receiver stack.
public class PushReceiverCont : Continuation
{
    public override void Execute(Evaluator eval)
    {
        eval.ImplicitReceivers.Add(eval.ValueStack.Pop());
    }
}

/// Returns the initialised object to the value stack as the expression's result.
///
/// It is read back off the receiver stack rather than remembered beforehand because a struct
/// receiver is rebuilt by every field write, so the finished value only exists here.
public class PopReceiverCont : Continuation
{
    public override void Execute(Evaluator eval)
    {
        var receiver = eval.ImplicitReceivers[^1];
        eval.ImplicitReceivers.RemoveAt(eval.ImplicitReceivers.Count - 1);
        eval.ValueStack.Push(receiver);
    }
}

public class BoxingCont : Continuation
{
    private readonly IConversionOperation _op;
    public BoxingCont(IConversionOperation op) => _op = op;
    public override void Execute(Evaluator eval)
    {
        var val = eval.ValueStack.Pop();
        var boxedObj = new BoxedObject(eval.GetTypeId(_op.Type!), val);
        var objId = eval.Heap.Allocate(boxedObj);
        eval.Recorder?.RecordNewObj(objId, boxedObj);
        eval.ValueStack.Push(new ObjectRef(objId));
    }
}

public class FieldReadCont : Continuation
{
    private readonly IFieldReferenceOperation _op;
    public FieldReadCont(IFieldReferenceOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        // A const (including an enum member) is folded by Roslyn, so there is nothing to read.
        if (_op.Field.HasConstantValue || _op.ConstantValue.HasValue)
        {
            var constant = _op.Field.HasConstantValue ? _op.Field.ConstantValue : _op.ConstantValue.Value;
            eval.ValueStack.Push(constant is null
                ? NullValue.Instance
                : new PrimitiveValue(System.Type.GetTypeCode(constant.GetType()), constant));
            return;
        }

        if (_op.Field.IsStatic)
        {
            eval.ValueStack.Push(eval.ReadReference(eval.StaticFieldRef(_op.Field)));
            return;
        }

        var instanceValue = eval.ValueStack.Pop();
        if (instanceValue is ObjectRef objRef)
        {
            if (eval.Heap.TryGet(objRef.ObjId, out var obj) && obj is ClassObject cls)
            {
                if (cls.Fields.TryGetValue(_op.Field.Name, out var val))
                {
                    eval.ValueStack.Push(val);
                }
                else
                {
                    eval.ValueStack.Push(UnsetValue.Instance);
                }
            }
            else
            {
                throw new Exception("Object not found or is not a class object");
            }
        }
        else if (instanceValue is StructValue strct)
        {
            if (strct.Fields.TryGetValue(_op.Field.Name, out var val))
            {
                eval.ValueStack.Push(val);
            }
            else
            {
                eval.ValueStack.Push(UnsetValue.Instance);
            }
        }
        else if (instanceValue is NullValue)
        {
            // Dereferencing null is a C# exception the user can catch, not a crash of ours.
            eval.UnwindingException = new BuiltinExceptionValue("NullReferenceException");
        }
        else
        {
            throw new Exception("Invalid instance for field read");
        }
    }
}

public class FieldLValueCont : Continuation
{
    private readonly IFieldReferenceOperation _op;
    public FieldLValueCont(IFieldReferenceOperation op) => _op = op;

    public override void Execute(Evaluator eval)
    {
        if (_op.Field.IsStatic)
        {
            eval.RefStack.Push(eval.StaticFieldRef(_op.Field));
            return;
        }

        if (_op.Instance == null)
        {
            throw new NotSupportedException($"Cannot assign to field '{_op.Field.Name}' with no instance.");
        }
        
        if (_op.Instance.Type?.IsValueType == true)
        {
            var structRef = eval.RefStack.Pop();
            eval.RefStack.Push(new StructFieldRef(structRef, _op.Field.Name));
        }
        else
        {
            var instanceValue = eval.ValueStack.Pop();
            if (instanceValue is ObjectRef objRef)
            {
                eval.RefStack.Push(new FieldRef(objRef.ObjId, _op.Field.Name));
            }
            else
            {
                throw new Exception("Invalid instance for field lvalue");
            }
        }
    }
}
