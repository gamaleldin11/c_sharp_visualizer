using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.FlowAnalysis;

class Program
{
    static void Main()
    {
        Console.WriteLine("=== [VERIFY-REFS] ===");
        string source1 = "class C{static void Main(){System.Console.WriteLine(1);}}";
        var tree1 = CSharpSyntaxTree.ParseText(source1, new CSharpParseOptions(LanguageVersion.Latest));
        var refs = Basic.Reference.Assemblies.Net80.References.All;
        var comp1 = CSharpCompilation.Create("Test1", new[] { tree1 }, refs, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var diags1 = comp1.GetDiagnostics();
        Console.WriteLine($"Diagnostics count: {diags1.Length}");
        foreach (var d in diags1) Console.WriteLine(d);
        
        Console.WriteLine("\n=== [VERIFY-IOP] ===");
        string source2 = "class C{static void Main(){ int x = 1; }}";
        var tree2 = CSharpSyntaxTree.ParseText(source2, new CSharpParseOptions(LanguageVersion.Latest));
        var comp2 = CSharpCompilation.Create("Test2", new[] { tree2 }, refs, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var model2 = comp2.GetSemanticModel(tree2);
        var methodNode = tree2.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var op2 = model2.GetOperation(methodNode);
        Console.WriteLine($"op?.GetType().Name: {op2?.GetType().Name}");
        Console.WriteLine($"op?.Kind: {op2?.Kind}");

        Console.WriteLine("\n=== [VERIFY-CHILDREN] ===");
        if (op2 != null)
        {
            var hasChildren = op2.GetType().GetProperty("Children") != null;
            var hasChildOperations = op2.GetType().GetProperty("ChildOperations") != null;
            Console.WriteLine($"Has Children property: {hasChildren}");
            Console.WriteLine($"Has ChildOperations property: {hasChildOperations}");
            // Let's also check if IOperation has ChildOperations
            Console.WriteLine($"typeof(IOperation) has ChildOperations: {typeof(IOperation).GetProperty("ChildOperations") != null}");
            Console.WriteLine($"typeof(IOperation) has Children: {typeof(IOperation).GetProperty("Children") != null}");
        }

        Console.WriteLine("\n=== [VERIFY-CFG] ===");
        string source3 = "class C{static void Main(){ for(int i=0;i<10;i++){} }}";
        var tree3 = CSharpSyntaxTree.ParseText(source3, new CSharpParseOptions(LanguageVersion.Latest));
        var comp3 = CSharpCompilation.Create("Test3", new[] { tree3 }, refs, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        var model3 = comp3.GetSemanticModel(tree3);
        var methodNode3 = tree3.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().First();
        var op3 = model3.GetOperation(methodNode3);
        if (op3 is IMethodBodyOperation methodBody)
        {
            var cfg = ControlFlowGraph.Create(methodBody);
            Console.WriteLine($"Blocks count: {cfg.Blocks.Length}");
            var edgesCount = cfg.Blocks.SelectMany(b => new[] { b.FallThroughSuccessor, b.ConditionalSuccessor }.Where(e => e != null)).Count();
            Console.WriteLine($"Successor edges count: {edgesCount}");
        }
        else
        {
            Console.WriteLine("Could not get BlockBody from IMethodBodyOperation");
        }
    }
}
