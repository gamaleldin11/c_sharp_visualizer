using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using CsViz.Analysis;
using CsViz.Trace;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace CsViz.Differential;

/// The correctness oracle.
///
/// Each corpus program is executed twice - once by the real .NET runtime, once by the
/// interpreter - and the two stdout streams must match exactly. Unlike the golden traces,
/// this needs no hand-authored expectations and cannot go stale: real C# is the expectation.
/// It is what catches the class of bug where the interpreter runs happily and produces the
/// wrong number, which no amount of "does it crash" testing finds.
///
/// Running the corpus in-process is safe because the corpus is checked in and trusted. User
/// submissions are never executed anywhere - that is the entire point of interpreting.
public class DifferentialTests
{
    private readonly ITestOutputHelper _output;
    public DifferentialTests(ITestOutputHelper output) => _output = output;

    private static readonly string CorpusDir = Path.Combine(AppContext.BaseDirectory, "corpus");

    public static TheoryData<string> CorpusFiles()
    {
        var data = new TheoryData<string>();
        if (!Directory.Exists(CorpusDir)) return data;
        foreach (var file in Directory.GetFiles(CorpusDir, "*.cs").OrderBy(f => f))
        {
            data.Add(Path.GetFileName(file));
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void InterpreterOutputMatchesRealDotNet(string filename)
    {
        var path = Path.Combine(CorpusDir, filename);
        var source = File.ReadAllText(path);

        // An optional sibling .stdin file feeds Console.ReadLine on both sides.
        var stdinPath = Path.ChangeExtension(path, ".stdin");
        string? stdin = File.Exists(stdinPath) ? File.ReadAllText(stdinPath) : null;

        var expected = RunOnRealDotNet(source, stdin);
        var actual = RunOnInterpreter(source, stdin);

        if (actual.Status == "runtime_error")
        {
            var messages = string.Join("; ", actual.Diagnostics.Select(d => $"{d.Id} line {d.Line}: {d.Message}"));
            Assert.Fail($"Interpreter could not run {filename}: {messages}");
        }

        Assert.Equal("ok", actual.Status);
        _output.WriteLine($"real .NET   : {Escape(expected)}");
        _output.WriteLine($"interpreter : {Escape(actual.Stdout)}");
        Assert.Equal(Normalise(expected), Normalise(actual.Stdout));
    }

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");

    /// Only line-ending differences are forgiven. Real .NET emits Environment.NewLine, which
    /// is CRLF on Windows and LF elsewhere, while the interpreter always emits LF - so
    /// comparing raw would make this suite pass or fail based on the operating system.
    private static string Normalise(string s) => s.Replace("\r\n", "\n").TrimEnd('\n');

    private record InterpreterRun(string Stdout, string Status, IReadOnlyList<DiagnosticDto> Diagnostics);

    private static InterpreterRun RunOnInterpreter(string source, string? stdin)
    {
        var result = TraceRunner.Run(new Compiler(), source, new TraceRunner.Options(Stdin: stdin));
        return new InterpreterRun(result.Stdout, result.Trace.Status, result.Trace.Diagnostics);
    }

    private static string RunOnRealDotNet(string source, string? stdin)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "DifferentialTarget_" + Guid.NewGuid().ToString("N"),
            new[] { tree },
            Basic.Reference.Assemblies.Net80.References.All,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, optimizationLevel: OptimizationLevel.Debug));

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);
            throw new InvalidOperationException(
                "Corpus program does not compile: " + string.Join("; ", errors.Select(d => d.ToString())));
        }

        peStream.Position = 0;

        // A collectible context keeps each corpus program's assembly from accumulating across
        // the run; without it the test host holds every compiled corpus assembly at once.
        var context = new AssemblyLoadContext("differential", isCollectible: true);
        var originalOut = Console.Out;
        var originalIn = Console.In;
        var captured = new StringWriter { NewLine = "\n" };

        try
        {
            var assembly = context.LoadFromStream(peStream);
            var entryPoint = assembly.EntryPoint
                ?? throw new InvalidOperationException("Corpus program has no entry point.");

            Console.SetOut(captured);
            Console.SetIn(new StringReader(stdin ?? ""));

            // A void Main takes no arguments in this corpus, but the CLR still passes string[]
            // when the signature declares it.
            var args = entryPoint.GetParameters().Length == 0
                ? null
                : new object?[] { Array.Empty<string>() };
            entryPoint.Invoke(null, args);
        }
        catch (TargetInvocationException ex)
        {
            // An uncaught exception still produced output up to that point, and the
            // interpreter should have produced the same prefix.
            captured.Write("");
            _ = ex;
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            context.Unload();
        }

        return captured.ToString();
    }
}
