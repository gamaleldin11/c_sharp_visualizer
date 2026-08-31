namespace CsViz.Ai;

/// Configuration for the AI features, all of it optional.
///
/// The visualizer must be fully usable with no key, no quota and no network. Everything here
/// therefore has a working default, and Enabled is false unless an API key is actually present.
public class AiOptions
{
    /// Read from the MISTRAL_API_KEY environment variable. Never sent to the browser, never
    /// logged, and never written into a trace.
    public string? ApiKey { get; set; }

    /// Master switch. Set CSVIZ_AI_ENABLED=false to turn the feature off even with a key
    /// present - useful for turning narration off in an incident without a redeploy.
    public bool Enabled { get; set; } = true;

    public string BaseUrl { get; set; } = "https://api.mistral.ai/v1/chat/completions";

    /// Narration is short, frequent and latency-sensitive; the small model is the right tool.
    public string NarrationModel { get; set; } = "mistral-small-latest";

    /// A crash post-mortem happens once, so it is worth a stronger model than narration.
    ///
    /// Deliberately medium, not large: mistral-large is not available on the free tier
    /// ("tier_not_allowed"), and this project is meant to cost nothing to run. Set
    /// CSVIZ_AI_EXPLAIN_MODEL to upgrade it on a paid key.
    public string ExplainModel { get; set; } = "mistral-medium-latest";

    /// Where the narration cache lives. A cache is what makes a free tier viable: teaching
    /// programs get narrated once, ever, and the steady-state hit rate approaches 100%.
    public string CachePath { get; set; } = "csviz-ai-cache.db";

    /// Requests per IP per minute. A public endpoint that proxies a paid API needs a ceiling
    /// that does not depend on the upstream provider noticing first.
    public int RequestsPerMinutePerIp { get; set; } = 20;

    /// Total upstream calls allowed per day across all users. When this is reached the AI
    /// panel reports itself unavailable and the rest of the app carries on unaffected.
    public int DailyCallBudget { get; set; } = 1000;

    public int TimeoutSeconds { get; set; } = 30;

    /// Reads configuration from the environment.
    public static AiOptions FromEnvironment()
    {
        var options = new AiOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("MISTRAL_API_KEY"),
        };

        if (Environment.GetEnvironmentVariable("CSVIZ_AI_ENABLED") is { } enabled)
        {
            options.Enabled = !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase);
        }

        if (Environment.GetEnvironmentVariable("CSVIZ_AI_EXPLAIN_MODEL") is { Length: > 0 } explainModel)
        {
            options.ExplainModel = explainModel;
        }

        if (Environment.GetEnvironmentVariable("CSVIZ_AI_NARRATION_MODEL") is { Length: > 0 } narrationModel)
        {
            options.NarrationModel = narrationModel;
        }

        if (Environment.GetEnvironmentVariable("CSVIZ_AI_CACHE") is { Length: > 0 } cachePath)
        {
            options.CachePath = cachePath;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("CSVIZ_AI_DAILY_BUDGET"), out var budget) && budget > 0)
        {
            options.DailyCallBudget = budget;
        }

        return options;
    }

    /// True only when the feature is switched on and a key is actually configured.
    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}
