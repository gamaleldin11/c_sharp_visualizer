using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using CsViz.Trace;

namespace CsViz.Ai;

public record NarrationResult(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("cached")] bool Cached);

public record ExplanationResult(
    [property: JsonPropertyName("cause")] string Cause,
    [property: JsonPropertyName("evidenceSteps")] List<int> EvidenceSteps,
    [property: JsonPropertyName("suggestedFix")] string SuggestedFix,
    [property: JsonPropertyName("fixedLine")] string? FixedLine,
    [property: JsonPropertyName("droppedCitations")] int DroppedCitations,
    [property: JsonPropertyName("cached")] bool Cached);

public record ChatResult(
    [property: JsonPropertyName("text")] string Text);

public record AiStatus(
    [property: JsonPropertyName("available")] bool Available,
    [property: JsonPropertyName("reason")] string? Reason,
    [property: JsonPropertyName("callsToday")] int CallsToday,
    [property: JsonPropertyName("dailyBudget")] int DailyBudget);

/// Narration and crash explanation, with every guard rail the plan calls for.
///
/// The ordering of checks matters and is deliberate: cache first (free and instant), then the
/// per-IP limit, then the global daily budget, and only then the network. A cached answer is
/// served even when the budget is exhausted, because it costs nothing.
public sealed class AiService : IDisposable
{
    private readonly AiOptions _options;
    private readonly MistralClient _client;
    private readonly NarrationCache _cache;

    // Per-IP token bucket. In memory because it only has to survive as long as a burst does;
    // the durable protection is the daily budget, which lives in SQLite.
    private readonly ConcurrentDictionary<string, (DateTime Window, int Count)> _rateLimit = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public AiService(AiOptions options, MistralClient client)
    {
        _options = options;
        _client = client;
        _cache = new NarrationCache(options.CachePath);
    }

    public AiStatus Status() => new(
        _options.IsUsable,
        _options.IsUsable ? null
            : !_options.Enabled ? "AI features are turned off."
            : "No Mistral API key is configured on the server.",
        _cache.CallsToday(),
        _options.DailyCallBudget);

    public bool AllowRequest(string clientId)
    {
        var now = DateTime.UtcNow;
        var window = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc);

        var updated = _rateLimit.AddOrUpdate(
            clientId,
            _ => (window, 1),
            (_, existing) => existing.Window == window ? (window, existing.Count + 1) : (window, 1));

        // Opportunistic cleanup so a long-running server does not retain an entry per visitor.
        if (_rateLimit.Count > 4096)
        {
            foreach (var (key, value) in _rateLimit)
            {
                if (value.Window < window) _rateLimit.TryRemove(key, out _);
            }
        }

        return updated.Count <= _options.RequestsPerMinutePerIp;
    }

    /// Narrates one step. Returns null when narration is unavailable for any reason.
    public async Task<NarrationResult?> NarrateAsync(TraceDto trace, int stepIndex, CancellationToken cancellationToken)
    {
        if (!_options.IsUsable) return null;
        if (stepIndex < 0 || stepIndex >= trace.Steps.Count) return null;

        var key = NarrationCache.KeyFor("narrate", trace.SourceHash, stepIndex, Prompts.DeltaSignature(trace, stepIndex));

        if (_cache.TryGet(key) is { } hit) return new NarrationResult(hit, Cached: true);

        if (!_cache.TryConsumeDailyBudget(_options.DailyCallBudget)) return null;

        var text = await _client.CompleteAsync(
            _options.NarrationModel,
            Prompts.NarratorSystem,
            Prompts.NarrateUser(trace, stepIndex),
            jsonMode: false,
            maxTokens: 220,
            cancellationToken);

        if (text == null) return null;

        _cache.Put(key, "narrate", text);
        return new NarrationResult(text, Cached: false);
    }

    /// Explains why a program crashed, with every citation checked against the real trace.
    public async Task<ExplanationResult?> ExplainAsync(TraceDto trace, CancellationToken cancellationToken)
    {
        if (!_options.IsUsable) return null;

        // The whole trace determines the explanation, so the step index in the key is a
        // constant and the signature is the status plus the diagnostics.
        var signature = trace.Status + "|" + string.Join(";",
            trace.Diagnostics.Where(d => d.Severity == 3).Select(d => $"{d.Id}@{d.Line}"));
        var key = NarrationCache.KeyFor("explain", trace.SourceHash, -1, signature);

        if (_cache.TryGet(key) is { } hit)
        {
            var cached = Parse(hit, trace, cached: true);
            if (cached != null) return cached;
        }

        if (!_cache.TryConsumeDailyBudget(_options.DailyCallBudget)) return null;

        var raw = await _client.CompleteAsync(
            _options.ExplainModel,
            Prompts.ExplainerSystem,
            Prompts.ExplainUser(trace),
            jsonMode: true,
            maxTokens: 600,
            cancellationToken);

        // Free-tier keys cannot reach the larger models. Falling back to the narration model
        // gives a somewhat weaker explanation instead of none at all, which is the right
        // trade for a tool that has to keep working on a key with no subscription.
        if (raw == null && _client.LastFailureWasModelAccess && _options.ExplainModel != _options.NarrationModel)
        {
            raw = await _client.CompleteAsync(
                _options.NarrationModel,
                Prompts.ExplainerSystem,
                Prompts.ExplainUser(trace),
                jsonMode: true,
                maxTokens: 600,
                cancellationToken);
        }

        if (raw == null) return null;

        var result = Parse(raw, trace, cached: false);
        if (result != null) _cache.Put(key, "explain", raw);
        return result;
    }

    /// Provides a conversational AI tutor for asking direct questions about the code.
    /// The user's question and the current execution state are sent to the model.
    public async Task<ChatResult?> ChatAsync(TraceDto? trace, int stepIndex, string message, CancellationToken cancellationToken)
    {
        if (!_options.IsUsable) return null;

        if (!_cache.TryConsumeDailyBudget(_options.DailyCallBudget)) return null;

        var systemPrompt = "You are an expert C# coding tutor. If the user asks you to write code, you MUST wrap it in a standard markdown C# code block (```csharp ... ```).";
        
        string userPrompt;
        if (trace != null && stepIndex >= 0 && stepIndex < trace.Steps.Count)
        {
            systemPrompt += " The user is stepping through a program. Help them understand it.";
            userPrompt = $"Here is the program context at step {stepIndex}:\n{Prompts.NarrateUser(trace, stepIndex)}\n\nUser Question: {message}";
        }
        else
        {
            userPrompt = $"User Question: {message}";
        }

        var text = await _client.CompleteAsync(
            _options.NarrationModel,
            systemPrompt,
            userPrompt,
            jsonMode: false,
            maxTokens: 1000,
            cancellationToken);

        if (text == null) return null;
        return new ChatResult(text);
    }

    private record RawExplanation(
        string? Cause,
        List<int>? EvidenceSteps,
        string? SuggestedFix,
        string? FixedLine);

    /// Parses the model's JSON and discards any citation that does not correspond to a real
    /// step.
    ///
    /// This is the grounding mechanism, and the reason the explainer is asked for step numbers
    /// at all: a fabricated citation is caught here rather than rendered as a link that jumps
    /// nowhere. The count of discarded citations is returned so the UI can be honest about it
    /// instead of quietly hiding the discrepancy.
    private static ExplanationResult? Parse(string raw, TraceDto trace, bool cached)
    {
        RawExplanation? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<RawExplanation>(raw, Json);
        }
        catch (JsonException)
        {
            return null;
        }

        if (parsed?.Cause == null) return null;

        var cited = parsed.EvidenceSteps ?? new List<int>();
        var valid = cited.Where(i => i >= 0 && i < trace.Steps.Count).Distinct().OrderBy(i => i).ToList();

        return new ExplanationResult(
            parsed.Cause,
            valid,
            parsed.SuggestedFix ?? "",
            string.IsNullOrWhiteSpace(parsed.FixedLine) ? null : parsed.FixedLine,
            DroppedCitations: cited.Count - valid.Count,
            cached);
    }

    public void Dispose() => _cache.Dispose();
}
