using System;
using System.Linq;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using CsViz.Core.Eval;
using CsViz.Core.Values;

namespace CsViz.Core.Tests;

public class TestMethodProvider : IMethodProvider
{
    public SemanticModel Model { get; }
    public SyntaxTree Tree { get; }

    public TestMethodProvider(SemanticModel model, SyntaxTree tree)
    {
        Model = model;
        Tree = tree;
    }

    public IOperation? GetMethodBody(IMethodSymbol method)
    {
        // For testing, find the method in the syntax tree
        var methodDecl = Tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == method.Name && 
                Model.GetDeclaredSymbol(m)?.ContainingType.Name == method.ContainingType.Name);

        if (methodDecl != null)
        {
            var op = Model.GetOperation(methodDecl);
            if (op is IMethodBodyOperation mb)
                return mb.BlockBody ?? mb.ExpressionBody;
        }
        
        return null;
    }
}

public static class TestHelper
{
    public static (Evaluator eval, IOperation rootOp) CompileAndSetup(string source)
    {
        // Need to add references
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>()
            .ToList();

        var tree = CSharpSyntaxTree.ParseText(source);
        var comp = CSharpCompilation.Create("TestComp", new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
        
        var diags = comp.GetDiagnostics();
        if (diags.Any(d => d.Severity == DiagnosticSeverity.Error))
        {
            throw new Exception("Compile errors: " + string.Join("\n", diags.Where(d => d.Severity == DiagnosticSeverity.Error)));
        }

        var model = comp.GetSemanticModel(tree);
        var provider = new TestMethodProvider(model, tree);
        
        var mainDecl = tree.GetRoot().DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Main");
            
        var mainOp = model.GetOperation(mainDecl) as IMethodBodyOperation;
        var rootOp = mainOp?.BlockBody;

        var eval = new Evaluator(provider);
        // Setup main frame
        var mainSymbol = model.GetDeclaredSymbol(mainDecl) as IMethodSymbol;
        var mainFrame = new Frames.Frame
        {
            Id = 1,
            Method = mainSymbol!
        };
        eval.PushFrame(mainFrame);
        
        return (eval, rootOp!);
    }
}
