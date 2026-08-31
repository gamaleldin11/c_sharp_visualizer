using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using CsViz.Analysis;
using CsViz.Core.Eval;
using CsViz.Core.Frames;

namespace CsViz.Trace;

/// Compiles a program and interprets it into a trace.
///
/// This is the one path from source to trace. The API, the golden tests and the differential
/// tests all call it, so a fix in any of them is a fix in all of them - previously each had
/// its own copy of the setup and they had already drifted (only the API pre-declared Main's
/// locals, so goldens recorded slot ids the UI would have rendered as "slot2").
public static class TraceRunner
{
    /// Recorded steps the user can scrub through.
    public const int DefaultMaxSteps = 15_000;

    /// Continuations the interpreter may execute. Much larger than the step budget because one
    /// statement costs many continuations.
    public const int DefaultMaxOperations = 500_000;

    public record Options(
        int MaxSteps = DefaultMaxSteps,
        string? Stdin = null,
        int MaxOperations = DefaultMaxOperations);

    public record RunResult(TraceDto Trace, string Stdout);

    public static string HashOf(string source, string? stdin)
    {
        // stdin is part of the key: Console.ReadLine consumes it, so the same source with
        // different input is a different trace and must not collide in any cache.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source + "\0" + (stdin ?? "")));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static RunResult Run(Compiler compiler, string source, Options? options = null)
    {
        options ??= new Options();
        var sourceHash = HashOf(source, options.Stdin);
        var compileResult = compiler.Compile(source);

        var diagnostics = compileResult.Diagnostics.Select(ToDto).ToList();

        if (!compileResult.Success || compileResult.EntryPoint == null || compileResult.Model == null)
        {
            var failed = new TraceDto(1, sourceHash, source, options.Stdin, "compile_error", null,
                diagnostics, new(), new(), new(), new(), new(), new());
            return new RunResult(failed, "");
        }

        // The flowchart and dataflow views come from the compilation, not from execution, so
        // they are available even when the program later hits a limit or throws.
        var methods = compileResult.Root != null
            ? StaticAnalysis.AnalyzeAll(compileResult.Model, compileResult.Root).Select(ToDto).ToList()
            : new List<MethodAnalysisDto>();

        var eval = new Evaluator(new SemanticModelMethodProvider(compileResult.Model))
        {
            MaxOperations = options.MaxOperations
        };
        eval.SetStdin(options.Stdin);

        var encoder = new TraceEncoder(eval) { MaxSteps = options.MaxSteps };
        eval.Recorder = encoder;

        if (compileResult.MainMethod != null)
        {
            var mainFrame = new Frame { Id = 1, Method = compileResult.MainMethod };
            // Pre-declare Main's locals exactly as MethodCallCont does for every other frame,
            // or locals in nested blocks get a slot id with no name and render as "slot2".
            MethodCallCont.DeclareLocals(compileResult.EntryPoint, mainFrame);
            eval.PushFrame(mainFrame);
        }

        string status = "ok";
        string? limitHit = null;

        try
        {
            eval.Run(compileResult.EntryPoint);
        }
        catch (LimitExceededException ex)
        {
            // Not an error: the partial trace up to the cutoff is still fully playable, which
            // is the whole point of showing a student their infinite loop.
            status = "limit_exceeded";
            limitHit = ex.Limit;
        }
        catch (UnhandledUserException ex)
        {
            // The program threw and nothing caught it. That is a result worth showing, not an
            // error page: the partial trace up to the throw is exactly what explains it.
            status = "runtime_error";
            var at = encoder.Steps.Count > 0 ? encoder.Steps[^1] : null;
            diagnostics.Add(new DiagnosticDto(
                3,
                at?.Line ?? 1,
                at?.Col ?? 1,
                at?.EndLine ?? 1,
                at?.EndCol ?? 1,
                $"{ex.ExceptionName}: {ex.Message}",
                ex.ExceptionName));
        }
        catch (UnsupportedConstructException ex)
        {
            status = "runtime_error";
            diagnostics.Add(new DiagnosticDto(3, ex.Line, ex.Column, ex.EndLine, ex.EndColumn,
                ex.Message, "CSVIZ0002"));
        }
        catch (Exception ex)
        {
            // Surfaced rather than swallowed. Returning a truncated trace with no explanation
            // makes an interpreter gap look like a bug in the user's own program.
            //
            // CSVIZ0001 always means an interpreter defect, never a defect in the traced
            // program, so the stack trace is what someone would actually need. It is gated on
            // CSVIZ_DEBUG because a public endpoint should not hand out internal frames.
            status = "runtime_error";
            var detail = Environment.GetEnvironmentVariable("CSVIZ_DEBUG") == "1"
                ? ex.ToString()
                : ex.Message;
            diagnostics.Add(new DiagnosticDto(3, 1, 1, 1, 1, detail, "CSVIZ0001"));
        }

        // Emit any statement still open - the program may have stopped mid-way through one,
        // and the step it was on is the most interesting one in a limit_exceeded trace.
        try
        {
            encoder.Finish();
        }
        catch (LimitExceededException ex)
        {
            status = "limit_exceeded";
            limitHit = ex.Limit;
        }

        var trace = encoder.BuildTrace(sourceHash, source, status, limitHit, diagnostics, options.Stdin, methods);
        return new RunResult(trace, encoder.Stdout);
    }

    private static MethodAnalysisDto ToDto(MethodAnalysis m) => new(
        m.Name,
        m.DeclaringType,
        m.StartLine,
        m.EndLine,
        m.Blocks.Select(b => new CfgBlockDto(
            b.Ordinal, b.Kind, b.Label, b.Lines.ToList(), b.Condition,
            b.FallThrough, b.ConditionalTarget, b.ConditionalLabel, b.Reachable)).ToList(),
        m.LineFacts.Select(f => new LineFactsDto(f.Line, f.Reads.ToList(), f.Writes.ToList())).ToList());

    private static DiagnosticDto ToDto(Diagnostic d)
    {
        var pos = d.Location.GetLineSpan();
        return new DiagnosticDto(
            (int)d.Severity,
            pos.StartLinePosition.Line + 1,
            pos.StartLinePosition.Character + 1,
            pos.EndLinePosition.Line + 1,
            pos.EndLinePosition.Character + 1,
            d.GetMessage(),
            d.Id);
    }
}

/// Resolves a method's body from the compilation the program was parsed from.
public class SemanticModelMethodProvider : IMethodProvider
{
    private readonly SemanticModel _model;
    public SemanticModelMethodProvider(SemanticModel model) => _model = model;

    public IOperation? GetMethodBody(IMethodSymbol method)
    {
        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syntax == null) return null;

        var op = _model.GetOperation(syntax);

        // A constructor is IConstructorBodyOperation, not IMethodBodyOperation. Missing that
        // made every user-defined constructor report as an unsupported construct.
        if (op is Microsoft.CodeAnalysis.Operations.IConstructorBodyOperation ctor)
        {
            return ctor.BlockBody ?? (IOperation?)ctor.ExpressionBody;
        }

        if (op is Microsoft.CodeAnalysis.Operations.IMethodBodyOperation mb)
        {
            // An expression-bodied member has no BlockBody, so falling straight through to
            // BlockBody would report every `int Get() => x;` as having no body at all.
            return mb.BlockBody ?? (IOperation?)mb.ExpressionBody;
        }
        return op;
    }

    /// The `: base(...)` or `: this(...)` clause, which runs before the constructor body.
    public IOperation? GetConstructorInitializer(IMethodSymbol constructor)
    {
        var syntax = constructor.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syntax == null) return null;
        return _model.GetOperation(syntax) is Microsoft.CodeAnalysis.Operations.IConstructorBodyOperation ctor
            ? ctor.Initializer
            : null;
    }

    public IReadOnlyList<Microsoft.CodeAnalysis.Operations.IFieldInitializerOperation> GetFieldInitializers(INamedTypeSymbol type)
    {
        if (_fieldInitializerCache.TryGetValue(type, out var cached)) return cached;

        var result = new List<Microsoft.CodeAnalysis.Operations.IFieldInitializerOperation>();
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsConst) continue;
            foreach (var reference in field.DeclaringSyntaxReferences)
            {
                // The initializer operation hangs off the `= value` clause, not off the
                // declarator that contains it. Asking about the declarator returns nothing at
                // all, which is why every field initializer was silently skipped: `public int
                // V = 7;` left V at 0, and a program reading it produced a confidently wrong
                // answer rather than an error.
                if (reference.GetSyntax() is not Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax
                    { Initializer: { } equalsValue })
                {
                    continue;
                }

                if (_model.GetOperation(equalsValue) is Microsoft.CodeAnalysis.Operations.IFieldInitializerOperation
                    { Value: not null } init)
                {
                    result.Add(init);
                }
            }
        }

        _fieldInitializerCache[type] = result;
        return result;
    }

    private readonly Dictionary<INamedTypeSymbol, IReadOnlyList<Microsoft.CodeAnalysis.Operations.IFieldInitializerOperation>>
        _fieldInitializerCache = new(SymbolEqualityComparer.Default);
}
