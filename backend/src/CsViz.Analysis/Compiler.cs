using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CsViz.Analysis;

public record CompilationResult(
    bool Success,
    SemanticModel? Model,
    IOperation? EntryPoint,
    IMethodSymbol? MainMethod,
    IReadOnlyList<Diagnostic> Diagnostics,
    SyntaxNode? Root = null
);

public class Compiler
{
    public CompilationResult Compile(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        
        var options = new CSharpCompilationOptions(OutputKind.ConsoleApplication);
        
        var comp = CSharpCompilation.Create(
            "VisualizerSource",
            new[] { tree },
            Basic.Reference.Assemblies.Net80.References.All,
            options
        );
        
        var diags = comp.GetDiagnostics();
        var errors = diags.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        
        if (errors.Any())
        {
            return new CompilationResult(false, null, null, null, errors);
        }
        
        var model = comp.GetSemanticModel(tree);
        var root = tree.GetRoot();
        
        var mainMethod = root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text == "Main");
            
        if (mainMethod == null)
        {
            var errDiag = Diagnostic.Create(
                new DiagnosticDescriptor("CSVIZ1", "No Main Method", "Could not find a 'Main' method", "Compilation", DiagnosticSeverity.Error, true),
                Location.None
            );
            return new CompilationResult(false, null, null, null, new[] { errDiag });
        }
        
        var mainOp = model.GetOperation(mainMethod);
        IOperation? bodyOp = null;
        
        if (mainOp is Microsoft.CodeAnalysis.Operations.IMethodBodyOperation mb && mb.BlockBody != null)
        {
            bodyOp = mb.BlockBody;
        }
        else if (mainMethod.Body != null)
        {
            bodyOp = model.GetOperation(mainMethod.Body);
        }
        else if (mainMethod.ExpressionBody != null)
        {
            bodyOp = model.GetOperation(mainMethod.ExpressionBody);
        }
        
        if (bodyOp == null)
        {
             var errDiag = Diagnostic.Create(
                new DiagnosticDescriptor("CSVIZ2", "Invalid Main Method", "Could not get operation for Main method body", "Compilation", DiagnosticSeverity.Error, true),
                mainMethod.GetLocation()
            );
            return new CompilationResult(false, null, null, null, new[] { errDiag });
        }
        
        var mainSymbol = model.GetDeclaredSymbol(mainMethod) as IMethodSymbol;
        
        return new CompilationResult(true, model, bodyOp, mainSymbol, diags, root);
    }
}
