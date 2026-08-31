using CsViz.Ai;
using CsViz.Analysis;
using CsViz.Trace;
using Xunit;

namespace CsViz.Ai.Tests;

/// Tests for the parts of the AI feature that must work without ever calling a model.
///
/// The guard rails - the budget, the cache, the citation check, and the graceful-degradation
/// path - are the parts that protect the user and the bill, so they are the parts that need to
/// be provable offline. Nothing here touches the network or needs an API key.
public class AiTests
{
    private static TraceDto Trace(string source) =>
        TraceRunner.Run(new Compiler(), source).Trace;

    private const string Sample = """
        class Program
        {
            static void Main()
            {
                int total = 0;
                for (int i = 1; i <= 3; i++) { total = total + i; }
                System.Console.WriteLine(total);
            }
        }
        """;

    [Fact]
    public void ServiceIsUnavailableWithoutAKey()
    {
        var options = new AiOptions { ApiKey = null, CachePath = TempDb() };
        using var service = new AiService(options, new MistralClient(new HttpClient(), options));

        var status = service.Status();

        Assert.False(status.Available);
        Assert.NotNull(status.Reason);
    }

    [Fact]
    public async Task NarrationReturnsNullWithoutAKeyRatherThanThrowing()
    {
        // The whole point: no key must degrade, never fail. An exception here would surface as
        // a 500 and make the visualizer look broken over an optional feature.
        var options = new AiOptions { ApiKey = null, CachePath = TempDb() };
        using var service = new AiService(options, new MistralClient(new HttpClient(), options));

        Assert.Null(await service.NarrateAsync(Trace(Sample), 0, CancellationToken.None));
        Assert.Null(await service.ExplainAsync(Trace(Sample), CancellationToken.None));
    }

    [Fact]
    public void DailyBudgetIsEnforcedAndPersisted()
    {
        var path = TempDb();

        using (var cache = new NarrationCache(path))
        {
            Assert.True(cache.TryConsumeDailyBudget(2));
            Assert.True(cache.TryConsumeDailyBudget(2));
            Assert.False(cache.TryConsumeDailyBudget(2));
        }

        // Reopened: the budget must survive a restart, because a restart is exactly when an
        // accidental loop would otherwise get a fresh allowance.
        using (var reopened = new NarrationCache(path))
        {
            Assert.Equal(2, reopened.CallsToday());
            Assert.False(reopened.TryConsumeDailyBudget(2));
        }
    }

    [Fact]
    public void CacheRoundTripsAndIsKeyedByContent()
    {
        using var cache = new NarrationCache(TempDb());

        var key = NarrationCache.KeyFor("narrate", "hash", 3, "sig");
        Assert.Null(cache.TryGet(key));

        cache.Put(key, "narrate", "explanation");
        Assert.Equal("explanation", cache.TryGet(key));

        // A different step, or a different delta for the same step, is a different entry.
        Assert.NotEqual(key, NarrationCache.KeyFor("narrate", "hash", 4, "sig"));
        Assert.NotEqual(key, NarrationCache.KeyFor("narrate", "hash", 3, "other"));
        Assert.Equal(key, NarrationCache.KeyFor("narrate", "hash", 3, "sig"));
    }

    [Fact]
    public void RateLimitAllowsThenBlocks()
    {
        var options = new AiOptions { RequestsPerMinutePerIp = 3, CachePath = TempDb() };
        using var service = new AiService(options, new MistralClient(new HttpClient(), options));

        Assert.True(service.AllowRequest("1.2.3.4"));
        Assert.True(service.AllowRequest("1.2.3.4"));
        Assert.True(service.AllowRequest("1.2.3.4"));
        Assert.False(service.AllowRequest("1.2.3.4"));

        // A different caller has its own allowance.
        Assert.True(service.AllowRequest("5.6.7.8"));
    }

    [Fact]
    public void PromptNamesVariablesRatherThanSlotNumbers()
    {
        var trace = Trace(Sample);

        // The step that assigns `total` inside the loop.
        var step = trace.Steps.First(s =>
            Prompts.DescribeDelta(trace, s).Any(d => d.StartsWith("total ", StringComparison.Ordinal)));

        var described = Prompts.DescribeDelta(trace, step);

        Assert.Contains(described, d => d.StartsWith("total ", StringComparison.Ordinal));
        Assert.DoesNotContain(described, d => d.Contains("slot", StringComparison.Ordinal));
    }

    [Fact]
    public void NarrationPromptCarriesOnlyTheStepInQuestion()
    {
        var trace = Trace(Sample);
        var prompt = Prompts.NarrateUser(trace, 2);

        // Nearby source only, never the whole trace: the prompt must not grow with the run.
        Assert.Contains("the line marked > is the one that ran", prompt);
        Assert.DoesNotContain("setLocal", prompt);
        Assert.True(prompt.Length < 2000, $"narration prompt was {prompt.Length} characters");
    }

    [Fact]
    public void CacheKeyIgnoresEditsThatDoNotChangeTheStep()
    {
        // A comment added at the end of the file changes the source hash but not what step 2
        // did. The delta signature is what lets the narration for that step still be reusable
        // between two otherwise-identical programs.
        var a = Trace(Sample);
        var b = Trace(Sample + "\n// a trailing comment\n");

        Assert.NotEqual(a.SourceHash, b.SourceHash);
        Assert.Equal(Prompts.DeltaSignature(a, 2), Prompts.DeltaSignature(b, 2));
    }

    private static string TempDb() =>
        Path.Combine(Path.GetTempPath(), $"csviz-ai-test-{Guid.NewGuid():N}.db");
}
