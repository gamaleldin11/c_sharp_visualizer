namespace CsViz.Core.Bridge;

public record SupportedGroup(string Title, string[] Items);

public record SupportManifest(
    SupportedGroup[] Language,
    SupportedGroup[] Library,
    string[] NotSupported);

/// What this visualizer can and cannot run, stated up front.
///
/// The plan names library-surface creep as a live risk: people reach for APIs nobody shimmed
/// and discover the boundary by hitting it. Publishing the boundary turns "it crashed" into
/// "that is not on the list", which is a different experience entirely.
///
/// Every entry below was checked against the running interpreter rather than inferred from the
/// code. That distinction earned its keep: an earlier draft of this file claimed
/// auto-properties worked and ref parameters did not, and both were wrong.
public static class Supported
{
    public static readonly SupportManifest Manifest = new(
        Language:
        [
            new("Types", [
                "classes and structs, with C# value-vs-reference semantics",
                "fields, constructors, constructor chaining, and field initializers",
                "inheritance, virtual and override, interfaces and interface dispatch",
                "enums, constants, and static fields",
                "generic methods",
            ]),
            new("Statements", [
                "if / else, while, do-while, for, foreach",
                "switch with constant and default cases",
                "break and continue, including out of nested loops",
                "try / catch / finally and throw",
            ]),
            new("Expressions", [
                "arithmetic with C# numeric promotion, overflow, checked and unchecked",
                "casts and conversions with C# truncation rules",
                "++ and --, prefix and postfix, and compound assignment",
                "?? and ?: , is, default, nameof",
                "string concatenation, comparison, and interpolation",
                "arrays, indexers, object and collection initializers",
                "ref and out parameters",
            ]),
        ],
        Library:
        [
            new("Console", ["Write", "WriteLine", "ReadLine"]),
            new("string", [
                "Length", "indexing", "Substring", "IndexOf", "Contains",
                "StartsWith", "EndsWith", "ToUpper", "ToLower", "Trim",
                "Equals", "ToString", "IsNullOrEmpty",
            ]),
            new("char", ["IsDigit", "IsLetter", "IsLetterOrDigit", "IsWhiteSpace", "IsUpper", "IsLower", "ToUpper", "ToLower"]),
            new("Math", ["every static method, run against the real .NET implementation"]),
            new("Parsing", ["int.Parse", "long.Parse", "double.Parse", "float.Parse", "decimal.Parse", "bool.Parse"]),
            new("Arrays", ["Length", "Rank", "GetLength", "single-dimension arrays only"]),
            new("List<T>", ["Add", "Count", "indexing", "Clear", "foreach"]),
            new("Dictionary<K,V>", ["Add", "Count", "indexing", "ContainsKey", "Clear", "foreach over entries"]),
            new("Stack<T>", ["Push", "Pop", "Peek", "Count", "Clear", "foreach"]),
            new("Queue<T>", ["Enqueue", "Dequeue", "Peek", "Count", "Clear", "foreach"]),
            new("Exceptions", ["construction with a message", "Message", "catch by type", "the built-in runtime exceptions"]),
        ],
        NotSupported:
        [
            "async / await, and iterators (yield)",
            "lambdas, delegates, events, and LINQ",
            "properties with hand-written accessors, and auto-properties (an expression-bodied getter does work)",
            "user-defined operators and conversions",
            "user-declared generic types (generic methods do work, and the collections above are built in)",
            "multi-dimensional arrays (int[,]) - jagged arrays are fine",
            "goto, unsafe code, and pointers",
            "switch expressions and pattern matching beyond constant cases",
            "string.Format, and ToString() on non-string types",
            "file, network, threading and reflection APIs - by design, since no user code is ever executed",
        ]);
}
