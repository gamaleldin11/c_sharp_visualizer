using System;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using CsViz.Core.Values;
using CsViz.Core.Heap;

namespace CsViz.Core.Bridge;

public static class BCLBridge
{
    public static bool TryInvoke(IMethodSymbol method, IValue instance, IValue[] args, Eval.Evaluator eval, out IValue result)
    {
        result = NullValue.Instance;
        var ns = method.ContainingNamespace.ToDisplayString();
        var type = method.ContainingType.Name;
        
        if (ns == "System" && type == "Console")
        {
            if (method.Name is "WriteLine" or "Write")
            {
                // A non-primitive argument used to print as an empty string. C# calls
                // ToString(), which for a plain object is its type name; printing nothing at
                // all makes the user's program look broken rather than under-supported.
                var text = args.Length > 0 ? Display(args[0], eval) : "";
                eval.Recorder?.RecordStdout(method.Name == "WriteLine" ? text + "\n" : text);
                return true;
            }
            if (method.Name == "ReadLine")
            {
                var line = eval.ReadStdinLine();
                result = line == null ? NullValue.Instance : new PrimitiveValue(TypeCode.String, line);
                return true;
            }
        }
        else if (ns == "System" && type == "String")
        {
            if (TryInvokeString(method, instance, args, eval, out result)) return true;
        }
        else if (ns == "System" && method.Name == "Parse" && args.Length == 1 &&
                 args[0] is PrimitiveValue { Value: string parseText })
        {
            // int.Parse / double.Parse / bool.Parse. InvariantCulture so that a program traced
            // on one machine parses identically on another.
            if (TryParse(method.ContainingType.SpecialType, parseText, out result)) return true;
            eval.UnwindingException = new BuiltinExceptionValue("FormatException");
            return true;
        }
        else if (ns == "System" && type == "Char" && args.Length == 1 &&
                 args[0] is PrimitiveValue { Value: char ch })
        {
            bool? predicate = method.Name switch
            {
                "IsDigit" => char.IsDigit(ch),
                "IsLetter" => char.IsLetter(ch),
                "IsLetterOrDigit" => char.IsLetterOrDigit(ch),
                "IsWhiteSpace" => char.IsWhiteSpace(ch),
                "IsUpper" => char.IsUpper(ch),
                "IsLower" => char.IsLower(ch),
                _ => null
            };
            if (predicate.HasValue)
            {
                result = new PrimitiveValue(TypeCode.Boolean, predicate.Value);
                return true;
            }
            if (method.Name == "ToUpper") { result = new PrimitiveValue(TypeCode.Char, char.ToUpperInvariant(ch)); return true; }
            if (method.Name == "ToLower") { result = new PrimitiveValue(TypeCode.Char, char.ToLowerInvariant(ch)); return true; }
        }
        else if (ns == "System" && type == "Math")
        {
            var mathType = typeof(System.Math);
            var m = mathType.GetMethod(method.Name, args.Select(a => GetSysType(a)).ToArray());
            if (m != null)
            {
                var sysArgs = args.Select(a => ((PrimitiveValue)a).Value).ToArray();
                var ret = m.Invoke(null, sysArgs);
                if (ret != null)
                {
                    result = new PrimitiveValue(Type.GetTypeCode(ret.GetType()), ret);
                }
                return true;
            }
        }
        
        else if (ns == "System" && type == "Array")
        {
            // `a.Length` reaches here as a call to Array.get_Length.
            if (instance is ObjectRef arrRef && eval.Heap.TryGet(arrRef.ObjId, out var arrObj) && arrObj is ArrayObject array)
            {
                if (method.Name is "get_Length" or "get_Count")
                {
                    result = new PrimitiveValue(TypeCode.Int32, array.Elems.Length);
                    return true;
                }
                if (method.Name == "get_Rank")
                {
                    result = new PrimitiveValue(TypeCode.Int32, array.Dims.Length);
                    return true;
                }
                if (method.Name == "GetLength" && args.Length == 1 && args[0] is PrimitiveValue { Value: int dim })
                {
                    result = new PrimitiveValue(TypeCode.Int32, array.Dims[dim]);
                    return true;
                }
            }
            if (instance is NullValue)
            {
                eval.UnwindingException = new BuiltinExceptionValue("NullReferenceException");
                return true;
            }
        }
        else if (IsBclException(method.ContainingType) && method.MethodKind == MethodKind.Constructor)
        {
            // BCL exception types have no source to interpret. They are allocated as an
            // ordinary object carrying just Message, which is the only member the trace and
            // the catch matcher ever read - the private state of the real Exception class is
            // not something a visualizer should display anyway.
            if (instance is ObjectRef exRef && eval.Heap.TryGet(exRef.ObjId, out var exObj) && exObj is ClassObject exCls)
            {
                exCls.Fields["Message"] = args.Length > 0 && args[0] is PrimitiveValue { Value: string msg }
                    ? new PrimitiveValue(TypeCode.String, msg)
                    : new PrimitiveValue(TypeCode.String, "Exception of type '" + method.ContainingType.ToDisplayString() + "' was thrown.");
                eval.Recorder?.RecordSetField(exRef.ObjId, "Message", exCls.Fields["Message"]);
            }
            return true;
        }
        else if (IsBclException(method.ContainingType) && method.Name == "get_Message")
        {
            if (instance is ObjectRef exRef && eval.Heap.TryGet(exRef.ObjId, out var exObj) &&
                exObj is ClassObject exCls && exCls.Fields.TryGetValue("Message", out var msgValue))
            {
                result = msgValue;
                return true;
            }
        }
        else if (ns == "System.Collections.Generic" && type == "List")
        {
            return TryInvokeList(method, instance, args, eval, out result);
        }
        else if (ns == "System.Collections.Generic" && type == "Dictionary")
        {
            return TryInvokeDict(method, instance, args, eval, out result);
        }
        else if (ns == "System.Collections.Generic" && type == "KeyValuePair")
        {
            if (instance is StructValue structVal)
            {
                if (method.Name == "get_Key" && structVal.Fields.TryGetValue("Key", out var k)) { result = k; return true; }
                if (method.Name == "get_Value" && structVal.Fields.TryGetValue("Value", out var v)) { result = v; return true; }
            }
        }
        else if (ns == "System.Collections.Generic" && type == "Stack")
        {
            return TryInvokeStack(method, instance, args, eval, out result);
        }
        else if (ns == "System.Collections.Generic" && type == "Queue")
        {
            return TryInvokeQueue(method, instance, args, eval, out result);
        }
        
        return false;
    }

    private static bool TryInvokeList(IMethodSymbol method, IValue instance, IValue[] args, Eval.Evaluator eval, out IValue result)
    {
        result = NullValue.Instance;
        if (method.MethodKind == MethodKind.Constructor)
        {
            if (instance is ObjectRef objRef && eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj is ClassObject clsObj)
            {
                var listObj = new ListObject(clsObj.TypeId, 0, 4, new IValue[4]);
                eval.Heap.Set(objRef.ObjId, listObj);
                eval.Recorder?.RecordNewObj(objRef.ObjId, listObj);
            }
            return true;
        }

        if (instance is ObjectRef oRef && eval.Heap.TryGet(oRef.ObjId, out var obj))
        {
            if (obj is ListObject list)
            {
                if (method.Name == "Add")
            {
                var item = args[0];
                if (list.Count >= list.Capacity)
                {
                    var newCap = list.Capacity == 0 ? 4 : list.Capacity * 2;
                    var newBacking = new IValue[newCap];
                    Array.Copy(list.Backing, newBacking, list.Count);
                    list = list with { Capacity = newCap, Backing = newBacking };
                }
                list.Backing[list.Count] = item;
                list = list with { Count = list.Count + 1 };
                eval.Heap.Set(oRef.ObjId, list);
                // Re-emit the whole object: Add can also have grown the backing array, and a
                // setElem delta alone would leave the client with the old Count and Capacity.
                eval.Recorder?.RecordNewObj(oRef.ObjId, list);
                return true;
            }
            if (method.Name == "get_Count")
            {
                result = new PrimitiveValue(TypeCode.Int32, list.Count);
                return true;
            }
            if (method.Name == "get_Item")
            {
                var index = (int)((PrimitiveValue)args[0]).Value;
                if (index < 0 || index >= list.Count) throw new Exception("ArgumentOutOfRangeException");
                result = list.Backing[index];
                return true;
            }
            if (method.Name == "set_Item")
            {
                var index = (int)((PrimitiveValue)args[0]).Value;
                if (index < 0 || index >= list.Count) throw new Exception("ArgumentOutOfRangeException");
                list.Backing[index] = args[1];
                eval.Recorder?.RecordSetElem(oRef.ObjId, index, args[1]);
                return true;
            }
            if (method.Name == "Clear")
            {
                list = list with { Count = 0, Backing = new IValue[4], Capacity = 4 };
                eval.Heap.Set(oRef.ObjId, list);
                eval.Recorder?.RecordNewObj(oRef.ObjId, list);
                return true;
            }
        }
        }

        return false;
    }

    private static bool TryInvokeDict(IMethodSymbol method, IValue instance, IValue[] args, Eval.Evaluator eval, out IValue result)
    {
        result = NullValue.Instance;
        if (method.MethodKind == MethodKind.Constructor)
        {
            if (instance is ObjectRef objRef && eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj is ClassObject clsObj)
            {
                var dictObj = new DictObject(clsObj.TypeId, new List<KeyValuePair<IValue, IValue>>());
                eval.Heap.Set(objRef.ObjId, dictObj);
                eval.Recorder?.RecordNewObj(objRef.ObjId, dictObj);
            }
            return true;
        }

        if (instance is ObjectRef oRef && eval.Heap.TryGet(oRef.ObjId, out var obj) && obj is DictObject dict)
        {
            if (method.Name == "Add")
            {
                // Simple list-based dictionary for now
                dict.Entries.Add(new KeyValuePair<IValue, IValue>(args[0], args[1]));
                eval.Recorder?.RecordNewObj(oRef.ObjId, dict);
                return true;
            }
            if (method.Name == "get_Count")
            {
                result = new PrimitiveValue(TypeCode.Int32, dict.Entries.Count);
                return true;
            }
            if (method.Name == "get_Item")
            {
                foreach (var kvp in dict.Entries)
                {
                    if (kvp.Key.Equals(args[0]))
                    {
                        result = kvp.Value;
                        return true;
                    }
                }
                throw new Exception("KeyNotFoundException");
            }
            if (method.Name == "set_Item")
            {
                for (int i = 0; i < dict.Entries.Count; i++)
                {
                    if (dict.Entries[i].Key.Equals(args[0]))
                    {
                        dict.Entries[i] = new KeyValuePair<IValue, IValue>(args[0], args[1]);
                        eval.Recorder?.RecordNewObj(oRef.ObjId, dict);
                        return true;
                    }
                }
                dict.Entries.Add(new KeyValuePair<IValue, IValue>(args[0], args[1]));
                return true;
            }
            if (method.Name == "ContainsKey")
            {
                bool found = false;
                foreach (var kvp in dict.Entries)
                {
                    if (kvp.Key.Equals(args[0])) { found = true; break; }
                }
                result = new PrimitiveValue(TypeCode.Boolean, found);
                return true;
            }
            if (method.Name == "Clear")
            {
                dict.Entries.Clear();
                eval.Recorder?.RecordNewObj(oRef.ObjId, dict);
                return true;
            }
        }
        return false;
    }

    private static bool TryInvokeStack(IMethodSymbol method, IValue instance, IValue[] args, Eval.Evaluator eval, out IValue result)
    {
        result = NullValue.Instance;
        if (method.MethodKind == MethodKind.Constructor)
        {
            if (instance is ObjectRef objRef && eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj is ClassObject clsObj)
            {
                var stackObj = new StackObject(clsObj.TypeId, new List<IValue>());
                eval.Heap.Set(objRef.ObjId, stackObj);
                eval.Recorder?.RecordNewObj(objRef.ObjId, stackObj);
            }
            return true;
        }

        if (instance is ObjectRef oRef && eval.Heap.TryGet(oRef.ObjId, out var obj) && obj is StackObject stack)
        {
            if (method.Name == "Push")
            {
                stack.Items.Add(args[0]);
                eval.Recorder?.RecordNewObj(oRef.ObjId, stack);
                return true;
            }
            if (method.Name == "Pop")
            {
                if (stack.Items.Count == 0) throw new Exception("InvalidOperationException: Stack empty");
                var val = stack.Items[^1];
                stack.Items.RemoveAt(stack.Items.Count - 1);
                eval.Recorder?.RecordNewObj(oRef.ObjId, stack);
                result = val;
                return true;
            }
            if (method.Name == "Peek")
            {
                if (stack.Items.Count == 0) throw new Exception("InvalidOperationException: Stack empty");
                result = stack.Items[^1];
                return true;
            }
            if (method.Name == "get_Count")
            {
                result = new PrimitiveValue(TypeCode.Int32, stack.Items.Count);
                return true;
            }
            if (method.Name == "Clear")
            {
                stack.Items.Clear();
                eval.Recorder?.RecordNewObj(oRef.ObjId, stack);
                return true;
            }
        }
        return false;
    }

    private static bool TryInvokeQueue(IMethodSymbol method, IValue instance, IValue[] args, Eval.Evaluator eval, out IValue result)
    {
        result = NullValue.Instance;
        if (method.MethodKind == MethodKind.Constructor)
        {
            if (instance is ObjectRef objRef && eval.Heap.TryGet(objRef.ObjId, out var heapObj) && heapObj is ClassObject clsObj)
            {
                var queueObj = new QueueObject(clsObj.TypeId, new List<IValue>());
                eval.Heap.Set(objRef.ObjId, queueObj);
                eval.Recorder?.RecordNewObj(objRef.ObjId, queueObj);
            }
            return true;
        }

        if (instance is ObjectRef oRef && eval.Heap.TryGet(oRef.ObjId, out var obj) && obj is QueueObject queue)
        {
            if (method.Name == "Enqueue")
            {
                queue.Items.Add(args[0]);
                eval.Recorder?.RecordNewObj(oRef.ObjId, queue);
                return true;
            }
            if (method.Name == "Dequeue")
            {
                if (queue.Items.Count == 0) throw new Exception("InvalidOperationException: Queue empty");
                var val = queue.Items[0];
                queue.Items.RemoveAt(0);
                eval.Recorder?.RecordNewObj(oRef.ObjId, queue);
                result = val;
                return true;
            }
            if (method.Name == "Peek")
            {
                if (queue.Items.Count == 0) throw new Exception("InvalidOperationException: Queue empty");
                result = queue.Items[0];
                return true;
            }
            if (method.Name == "get_Count")
            {
                result = new PrimitiveValue(TypeCode.Int32, queue.Items.Count);
                return true;
            }
            if (method.Name == "Clear")
            {
                queue.Items.Clear();
                eval.Recorder?.RecordNewObj(oRef.ObjId, queue);
                return true;
            }
        }
        return false;
    }

    /// A type that derives from System.Exception and has no source we could interpret.
    public static bool IsBclException(ITypeSymbol? type)
    {
        if (type == null || type.DeclaringSyntaxReferences.Length > 0) return false;
        for (var current = type; current != null; current = current.BaseType)
        {
            if (current.ToDisplayString() == "System.Exception") return true;
        }
        return false;
    }

    private static Type GetSysType(IValue v)
    {
        if (v is PrimitiveValue pv) return pv.Value.GetType();
        return typeof(object);
    }

    /// What C# would print for a value.
    ///
    /// Object.ToString() defaults to the fully-qualified type name, and users do write
    /// Console.WriteLine(someObject) by accident - showing them the type name is the same
    /// feedback real .NET gives, and is what the differential test compares against.
    public static string Display(IValue value, Eval.Evaluator eval)
    {
        switch (value)
        {
            case NullValue:
                return "";
            case PrimitiveValue:
                return Eval.BinaryOps.Stringify(value);
            case StructValue sv:
                return sv.Type.ToDisplayString();
            case ObjectRef objRef when eval.Heap.TryGet(objRef.ObjId, out var obj) && obj != null:
                return obj switch
                {
                    BoxedObject boxed => Display(boxed.Value, eval),
                    ArrayObject arr => eval.GetTypeSymbol(arr.TypeId).ToDisplayString(),
                    _ => eval.GetTypeSymbol(obj.TypeId).ToDisplayString()
                };
            default:
                return "";
        }
    }

    private static bool TryParse(SpecialType target, string text, out IValue result)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        switch (target)
        {
            case SpecialType.System_Int32 when int.TryParse(text, System.Globalization.NumberStyles.Integer, culture, out var i):
                result = new PrimitiveValue(TypeCode.Int32, i);
                return true;
            case SpecialType.System_Int64 when long.TryParse(text, System.Globalization.NumberStyles.Integer, culture, out var l):
                result = new PrimitiveValue(TypeCode.Int64, l);
                return true;
            case SpecialType.System_Double when double.TryParse(text, System.Globalization.NumberStyles.Float, culture, out var d):
                result = new PrimitiveValue(TypeCode.Double, d);
                return true;
            case SpecialType.System_Single when float.TryParse(text, System.Globalization.NumberStyles.Float, culture, out var f):
                result = new PrimitiveValue(TypeCode.Single, f);
                return true;
            case SpecialType.System_Decimal when decimal.TryParse(text, System.Globalization.NumberStyles.Number, culture, out var m):
                result = new PrimitiveValue(TypeCode.Decimal, m);
                return true;
            case SpecialType.System_Boolean when bool.TryParse(text, out var b):
                result = new PrimitiveValue(TypeCode.Boolean, b);
                return true;
            default:
                result = NullValue.Instance;
                return false;
        }
    }

    /// The pure, side-effect-free slice of System.String.
    ///
    /// These are reflection-free hand implementations rather than calls into the real BCL:
    /// the whitelist stays explicit, and every out-of-range case raises the interpreter's own
    /// catchable exception instead of a host exception that would abort the whole trace.
    private static bool TryInvokeString(IMethodSymbol method, IValue instance, IValue[] args, Eval.Evaluator eval, out IValue result)
    {
        result = NullValue.Instance;

        if (instance is not PrimitiveValue { Value: string s })
        {
            if (method.Name == "IsNullOrEmpty" && args.Length == 1)
            {
                result = new PrimitiveValue(TypeCode.Boolean,
                    args[0] is NullValue || args[0] is PrimitiveValue { Value: "" });
                return true;
            }
            return false;
        }

        switch (method.Name)
        {
            case "get_Length":
                result = new PrimitiveValue(TypeCode.Int32, s.Length);
                return true;

            case "get_Chars":
                if (args[0] is PrimitiveValue { Value: int ci })
                {
                    if (ci < 0 || ci >= s.Length)
                    {
                        eval.UnwindingException = new BuiltinExceptionValue("IndexOutOfRangeException");
                        return true;
                    }
                    result = new PrimitiveValue(TypeCode.Char, s[ci]);
                    return true;
                }
                return false;

            case "Substring":
                {
                    int start = args[0] is PrimitiveValue { Value: int st } ? st : 0;
                    int len = args.Length > 1 && args[1] is PrimitiveValue { Value: int ln } ? ln : s.Length - start;
                    if (start < 0 || len < 0 || start + len > s.Length)
                    {
                        eval.UnwindingException = new BuiltinExceptionValue("ArgumentOutOfRangeException");
                        return true;
                    }
                    result = new PrimitiveValue(TypeCode.String, s.Substring(start, len));
                    return true;
                }

            case "ToUpper":
                result = new PrimitiveValue(TypeCode.String, s.ToUpperInvariant());
                return true;
            case "ToLower":
                result = new PrimitiveValue(TypeCode.String, s.ToLowerInvariant());
                return true;
            case "Trim":
                result = new PrimitiveValue(TypeCode.String, s.Trim());
                return true;
            case "ToString":
                result = new PrimitiveValue(TypeCode.String, s);
                return true;

            case "IndexOf":
            case "Contains":
            case "StartsWith":
            case "EndsWith":
                {
                    string needle = args[0] switch
                    {
                        PrimitiveValue { Value: string ns } => ns,
                        PrimitiveValue { Value: char nc } => nc.ToString(),
                        _ => ""
                    };
                    result = method.Name switch
                    {
                        "IndexOf" => new PrimitiveValue(TypeCode.Int32, s.IndexOf(needle, StringComparison.Ordinal)),
                        "Contains" => new PrimitiveValue(TypeCode.Boolean, s.Contains(needle, StringComparison.Ordinal)),
                        "StartsWith" => new PrimitiveValue(TypeCode.Boolean, s.StartsWith(needle, StringComparison.Ordinal)),
                        _ => new PrimitiveValue(TypeCode.Boolean, s.EndsWith(needle, StringComparison.Ordinal))
                    };
                    return true;
                }

            case "Equals":
                result = new PrimitiveValue(TypeCode.Boolean,
                    args.Length > 0 && args[0] is PrimitiveValue { Value: string other } &&
                    string.Equals(s, other, StringComparison.Ordinal));
                return true;

            default:
                return false;
        }
    }
}
