using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsViz.Analysis;
using CsViz.Trace;
using Xunit;

namespace CsViz.Differential;

/// The same program must always produce byte-identical trace JSON.
///
/// This is not a nicety. The trace cache and the /t/{hash} permalinks are keyed by the source
/// hash, so a trace that varies between runs means two users following the same link see
/// different diagrams. It also makes every golden test intermittently red, which trains people
/// to ignore them.
///
/// The failure this was written for: struct fields lived in an ImmutableDictionary and were
/// serialised in enumeration order. .NET randomises string hash codes per process, so the
/// field order changed between runs.
public class DeterminismTests
{
    private static readonly string CorpusDir = Path.Combine(AppContext.BaseDirectory, "corpus");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static TheoryData<string> CorpusFiles() => DifferentialTests.CorpusFiles();

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void RepeatedRunsProduceIdenticalTraces(string filename)
    {
        var path = Path.Combine(CorpusDir, filename);
        var source = File.ReadAllText(path);
        var stdinPath = Path.ChangeExtension(path, ".stdin");
        string? stdin = File.Exists(stdinPath) ? File.ReadAllText(stdinPath) : null;

        string Trace() => JsonSerializer.Serialize(
            TraceRunner.Run(new Compiler(), source, new TraceRunner.Options(Stdin: stdin)).Trace, Json);

        var first = Trace();
        var second = Trace();

        Assert.Equal(first, second);
    }

    [Fact]
    public void SourceHashCoversStdin()
    {
        // Console.ReadLine consumes stdin, so the same source with different input is a
        // different trace and must not collide in the cache.
        const string source = "class Program { static void Main() { System.Console.WriteLine(System.Console.ReadLine()); } }";

        Assert.NotEqual(TraceRunner.HashOf(source, "a"), TraceRunner.HashOf(source, "b"));
        Assert.Equal(TraceRunner.HashOf(source, "a"), TraceRunner.HashOf(source, "a"));
    }
}
