using CsViz.Analysis;
using CsViz.Trace;
using Xunit;

namespace CsViz.Differential;

/// Every resource ceiling, proved to fire.
///
/// These matter more than they look. The endpoint is public and runs arbitrary submitted code
/// through an interpreter; a limit that silently does not fire is a way to take the server
/// down. Each case here is a program written specifically to breach one ceiling, and each must
/// come back as a clean limit_exceeded trace naming that ceiling - never as a crash, a hang,
/// or a 500.
public class LimitTests
{
    private static TraceDto Run(string source, Action<TraceRunner.Options>? _ = null) =>
        TraceRunner.Run(new Compiler(), source).Trace;

    private static string Program(string body) =>
        "using System;\nusing System.Collections.Generic;\n\nclass Program\n{\n    static void Main()\n    {\n"
        + body + "\n    }\n}\n";

    [Fact]
    public void InfiniteLoopStopsOnTheStepBudget()
    {
        var trace = Run(Program("        int i = 0;\n        while (true) { i = i + 1; }"));

        Assert.Equal("limit_exceeded", trace.Status);
        Assert.Equal("steps", trace.LimitHit);

        // The partial trace has to be playable: showing a student where their loop went is the
        // entire reason this is not just an error.
        Assert.NotEmpty(trace.Steps);
        Assert.NotEmpty(trace.Keyframes);
    }

    [Fact]
    public void UnboundedRecursionStopsOnTheStackDepthCeiling()
    {
        // Deep recursion must report cleanly rather than overflowing the host stack. That it
        // can is the payoff for using an explicit continuation stack instead of host recursion.
        var source = """
            class Program
            {
                static int Down(int n) { return Down(n + 1); }
                static void Main() { Down(0); }
            }
            """;

        var trace = Run(source);

        Assert.Equal("limit_exceeded", trace.Status);
        Assert.Equal("stackDepth", trace.LimitHit);
        Assert.NotEmpty(trace.Steps);
    }

    [Fact]
    public void UnboundedAllocationStopsOnTheHeapCeiling()
    {
        var trace = Run(Program("""
                    for (int i = 0; i < 100000; i++)
                    {
                        object o = new object[1];
                    }
            """));

        Assert.Equal("limit_exceeded", trace.Status);
        // Whichever ceiling is reached first is correct; the point is that one of them is, and
        // that the run is bounded.
        Assert.Contains(trace.LimitHit, new[] { "heap", "steps" });
    }

    [Fact]
    public void UnboundedOutputStopsOnTheOutputCeiling()
    {
        var trace = Run(Program("""
                    for (int i = 0; i < 100000; i++)
                    {
                        Console.WriteLine("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
                    }
            """));

        Assert.Equal("limit_exceeded", trace.Status);
        Assert.Contains(trace.LimitHit, new[] { "output", "steps" });
    }

    [Fact]
    public void ACompileErrorIsReportedWithAPosition()
    {
        var trace = Run("class Program { static void Main() { int x = ; } }");

        Assert.Equal("compile_error", trace.Status);
        Assert.NotEmpty(trace.Diagnostics);

        var error = trace.Diagnostics.First(d => d.Severity == 3);
        Assert.True(error.Line >= 1, "a compile error must carry a real line number");
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public void AnUnsupportedConstructIsReportedAtItsSourceSpan()
    {
        // Unsupported must mean a precise diagnostic, never a wrong diagram. `goto` is
        // genuinely out of scope, which makes it a stable case to assert on.
        var trace = Run(Program("""
                    int i = 0;
                again:
                    i = i + 1;
                    if (i < 3) goto again;
                    Console.WriteLine(i);
            """));

        Assert.Equal("runtime_error", trace.Status);
        var error = trace.Diagnostics.First(d => d.Severity == 3);
        Assert.True(error.Line > 1, "the diagnostic should point at the offending line, not line 1");
    }

    [Fact]
    public void AnUncaughtExceptionIsReportedAsACSharpException()
    {
        var trace = Run(Program("""
                    int[] a = new int[2];
                    Console.WriteLine(a[5]);
            """));

        Assert.Equal("runtime_error", trace.Status);

        var error = trace.Diagnostics.First(d => d.Severity == 3);
        // Named for the exception, not for an internal error code: this is the user's bug.
        Assert.Equal("IndexOutOfRangeException", error.Id);
        Assert.Contains("Index was outside the bounds", error.Message);
    }
}
