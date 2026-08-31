using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;

namespace CsViz.Analysis;

/// One basic block of a method's control-flow graph.
public record CfgBlock(
    int Ordinal,
    string Kind,
    string Label,
    IReadOnlyList<int> Lines,
    string? Condition,
    int? FallThrough,
    int? ConditionalTarget,
    string? ConditionalLabel,
    bool Reachable);

/// A method's control-flow graph plus the per-line variable facts the dataflow view needs.
public record MethodAnalysis(
    string Name,
    string DeclaringType,
    int StartLine,
    int EndLine,
    IReadOnlyList<CfgBlock> Blocks,
    IReadOnlyList<LineFacts> LineFacts);

/// Which variables a single source line reads and writes, as determined statically.
///
/// The trace records writes but not reads, and recording every read would multiply its size
/// for information that is almost entirely derivable. Pairing these static read sets with the
/// dynamic writes in the delta stream gives the dataflow view real def-use edges - each read
/// resolves to the last recorded write of that variable - without inflating the trace.
public record LineFacts(int Line, IReadOnlyList<string> Reads, IReadOnlyList<string> Writes);

/// Derives the flowchart and dataflow views.
///
/// Everything here is computed from the compilation, deterministically, and never by the
/// language model: the LLM narrates a diagram, it does not invent one.
public static class StaticAnalysis
{
    public static List<MethodAnalysis> AnalyzeAll(SemanticModel model, SyntaxNode root)
    {
        var results = new List<MethodAnalysis>();

        foreach (var node in root.DescendantNodes())
        {
            if (node is not BaseMethodDeclarationSyntax declaration) continue;
            if (declaration.Body == null && declaration.ExpressionBody == null) continue;

            var symbol = model.GetDeclaredSymbol(declaration) as IMethodSymbol;
            if (symbol == null) continue;

            var analysis = TryAnalyze(model, declaration, symbol);
            if (analysis != null) results.Add(analysis);
        }

        return results;
    }

    private static MethodAnalysis? TryAnalyze(SemanticModel model, BaseMethodDeclarationSyntax declaration, IMethodSymbol symbol)
    {
        ControlFlowGraph graph;
        try
        {
            graph = ControlFlowGraph.Create(declaration, model);
        }
        catch (Exception)
        {
            // A construct Roslyn cannot build a graph for should cost us the flowchart for one
            // method, not the whole trace. Every other view still works.
            return null;
        }

        var span = declaration.GetLocation().GetLineSpan();
        var blocks = graph.Blocks.Select(ToBlock).ToList();
        var facts = CollectLineFacts(model, declaration);

        return new MethodAnalysis(
            symbol.Name,
            symbol.ContainingType?.Name ?? "",
            span.StartLinePosition.Line + 1,
            span.EndLinePosition.Line + 1,
            blocks,
            facts);
    }

    private static CfgBlock ToBlock(BasicBlock block)
    {
        var lines = new SortedSet<int>();
        var statements = new List<string>();

        foreach (var operation in block.Operations)
        {
            foreach (var line in LinesOf(operation)) lines.Add(line);
            var text = Summarise(operation.Syntax.ToString());
            if (text.Length > 0) statements.Add(text);
        }

        string? condition = null;
        if (block.BranchValue != null)
        {
            condition = Summarise(block.BranchValue.Syntax.ToString());
            foreach (var line in LinesOf(block.BranchValue)) lines.Add(line);
        }

        // ConditionKind says which way the *conditional* edge goes: WhenTrue means the
        // conditional successor is taken when the branch value is true. Labelling the edges
        // from this rather than assuming is what keeps if/else and while drawn the right way
        // round.
        string? conditionalLabel = block.ConditionKind switch
        {
            ControlFlowConditionKind.WhenTrue => "true",
            ControlFlowConditionKind.WhenFalse => "false",
            _ => null
        };

        var kind = block.Kind switch
        {
            BasicBlockKind.Entry => "entry",
            BasicBlockKind.Exit => "exit",
            _ => "block"
        };

        var label = statements.Count > 0
            ? string.Join("\n", statements)
            : kind switch { "entry" => "start", "exit" => "end", _ => condition ?? "" };

        return new CfgBlock(
            block.Ordinal,
            kind,
            label,
            lines.ToList(),
            condition,
            block.FallThroughSuccessor?.Destination?.Ordinal,
            block.ConditionalSuccessor?.Destination?.Ordinal,
            conditionalLabel,
            block.IsReachable);
    }

    private static IEnumerable<int> LinesOf(IOperation operation)
    {
        var span = operation.Syntax.GetLocation().GetLineSpan();
        for (int line = span.StartLinePosition.Line; line <= span.EndLinePosition.Line; line++)
        {
            yield return line + 1;
        }
    }

    /// Collapses a statement to one readable line for a flowchart node.
    private static string Summarise(string text)
    {
        var flattened = string.Join(" ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return flattened.Length > 48 ? flattened[..48] + "..." : flattened;
    }

    /// Static reads and writes per source line.
    ///
    /// Roslyn's AnalyzeDataFlow works on a syntax span, so this asks it about each statement
    /// individually and keys the answer by the statement's first line. A line holding two
    /// statements gets their union, which is the right answer for the view.
    private static List<LineFacts> CollectLineFacts(SemanticModel model, BaseMethodDeclarationSyntax declaration)
    {
        var reads = new Dictionary<int, SortedSet<string>>();
        var writes = new Dictionary<int, SortedSet<string>>();

        var body = (SyntaxNode?)declaration.Body ?? declaration.ExpressionBody;
        if (body == null) return new List<LineFacts>();

        foreach (var node in DataFlowTargets(body))
        {
            DataFlowAnalysis? flow;
            try
            {
                flow = node switch
                {
                    StatementSyntax s => model.AnalyzeDataFlow(s),
                    ExpressionSyntax e => model.AnalyzeDataFlow(e),
                    _ => null
                };
            }
            catch (ArgumentException)
            {
                // Roslyn refuses spans it considers unanalyzable. One skipped line costs a few
                // dataflow edges, never the view.
                continue;
            }

            if (flow == null || !flow.Succeeded) continue;

            int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            if (!reads.TryGetValue(line, out var readSet)) reads[line] = readSet = new SortedSet<string>(StringComparer.Ordinal);
            if (!writes.TryGetValue(line, out var writeSet)) writes[line] = writeSet = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var symbol in flow.ReadInside) readSet.Add(symbol.Name);
            foreach (var symbol in flow.WrittenInside) writeSet.Add(symbol.Name);
        }

        return reads.Keys.Union(writes.Keys).OrderBy(l => l).Select(line => new LineFacts(

            line,
            reads.TryGetValue(line, out var r) ? r.ToList() : new List<string>(),
            writes.TryGetValue(line, out var w) ? w.ToList() : new List<string>())).ToList();
    }

    /// The syntax nodes worth asking Roslyn about, one per line of real work.
    ///
    /// Analysing a compound statement whole would attribute every variable in the body to the
    /// line of its opening keyword, so only leaf statements are analysed. Their controlling
    /// expressions have to be added back explicitly, or `if (i == 3)` and `while (n &lt; 4)`
    /// report reading nothing at all - which is exactly the line a reader most wants explained.
    private static IEnumerable<SyntaxNode> DataFlowTargets(SyntaxNode body)
    {
        foreach (var statement in body.DescendantNodes().OfType<StatementSyntax>())
        {
            switch (statement)
            {
                case BlockSyntax:
                    continue;

                case IfStatementSyntax ifStatement:
                    yield return ifStatement.Condition;
                    continue;

                case WhileStatementSyntax whileStatement:
                    yield return whileStatement.Condition;
                    continue;

                case DoStatementSyntax doStatement:
                    yield return doStatement.Condition;
                    continue;

                case SwitchStatementSyntax switchStatement:
                    yield return switchStatement.Expression;
                    continue;

                case ForEachStatementSyntax forEach:
                    yield return forEach.Expression;
                    continue;

                case ForStatementSyntax forStatement:
                    // The header's three clauses sit on one line but are separate spans.
                    if (forStatement.Declaration != null) yield return forStatement.Declaration;
                    if (forStatement.Condition != null) yield return forStatement.Condition;
                    foreach (var incrementor in forStatement.Incrementors) yield return incrementor;
                    continue;

                default:
                    // Anything else that still contains statements is a wrapper we have already
                    // descended into (checked, lock, using, try).
                    if (statement.ChildNodes().OfType<StatementSyntax>().Any()) continue;
                    yield return statement;
                    continue;
            }
        }
    }
}
