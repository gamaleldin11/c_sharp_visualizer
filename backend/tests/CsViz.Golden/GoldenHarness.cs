using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsViz.Analysis;
using CsViz.Trace;
using Xunit;

namespace CsViz.Golden;

/// Checked-in expected traces.
///
/// The differential suite proves the interpreter computes the right answers; these prove it
/// emits the right *trace* - the delta stream, keyframes, slot names and heap shapes the
/// frontend actually replays. A semantics-preserving change that silently stops emitting
/// setField deltas would pass every differential test and break the memory view completely.
///
/// Only the programs listed in golden.txt are snapshotted. Goldens are for shape, and a
/// hundred-thousand-line expectation file for a recursive Fibonacci is not reviewable, which
/// makes it worse than no golden at all - nobody can tell a real regression from churn.
public class GoldenHarness
{
    private static readonly string CorpusDir = Path.Combine(AppContext.BaseDirectory, "corpus");
    // Goldens are read from and written to the source tree, not the build output: a golden
    // that a developer has to hand-copy out of bin/ before committing is a golden that ends up
    // stale. bin/Debug/net10.0 is three levels below the project directory.
    private static readonly string ExpectedDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "expected"));

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static TheoryData<string> GoldenFiles()
    {
        var data = new TheoryData<string>();
        var manifest = Path.Combine(CorpusDir, "golden.txt");
        if (!File.Exists(manifest)) return data;

        foreach (var line in File.ReadAllLines(manifest))
        {
            var name = line.Trim();
            if (name.Length == 0 || name.StartsWith('#')) continue;
            data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(GoldenFiles))]
    public void TraceMatchesGolden(string filename)
    {
        var sourcePath = Path.Combine(CorpusDir, filename);
        Assert.True(File.Exists(sourcePath), $"golden.txt lists {filename}, which is not in the corpus.");

        var source = File.ReadAllText(sourcePath);
        var stdinPath = Path.ChangeExtension(sourcePath, ".stdin");
        string? stdin = File.Exists(stdinPath) ? File.ReadAllText(stdinPath) : null;

        // Deliberately the same entry point the API uses, so a golden cannot pass against a
        // pipeline the product does not actually run.
        var result = TraceRunner.Run(new Compiler(), source, new TraceRunner.Options(Stdin: stdin));

        Assert.True(result.Trace.Status == "ok",
            $"{filename} did not run cleanly: " +
            string.Join("; ", result.Trace.Diagnostics.Where(d => d.Severity == 3).Select(d => d.Message)));

        var actual = JsonSerializer.Serialize(result.Trace, Json);
        var expectedPath = Path.Combine(ExpectedDir, Path.ChangeExtension(filename, ".json"));

        if (!File.Exists(expectedPath))
        {
            Directory.CreateDirectory(ExpectedDir);
            File.WriteAllText(expectedPath, actual);
            Assert.Fail($"No golden for {filename}; one has been written to {expectedPath}. Review and commit it.");
        }

        var expected = File.ReadAllText(expectedPath);
        Assert.Equal(Normalise(expected), Normalise(actual));
    }

    /// Line endings only. The goldens are text files that git may check out with either.
    private static string Normalise(string s) => s.Replace("\r\n", "\n").Trim();
}
