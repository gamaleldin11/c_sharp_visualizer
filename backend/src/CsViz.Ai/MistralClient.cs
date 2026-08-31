using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CsViz.Ai;

/// Minimal client for Mistral's OpenAI-compatible chat completions endpoint.
///
/// Hand-rolled rather than pulling in an SDK: the surface used here is one POST with a
/// messages array, and a dependency that wraps that is a dependency to keep patched for no
/// benefit.
public sealed class MistralClient
{
    private readonly HttpClient _http;
    private readonly AiOptions _options;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// True when the most recent failure was the API refusing the requested model.
    public bool LastFailureWasModelAccess { get; private set; }

    public MistralClient(HttpClient http, AiOptions options)
    {
        _http = http;
        _options = options;
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
    }

    private record Message(string Role, string Content);

    private record Request(
        string Model,
        List<Message> Messages,
        double Temperature,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("response_format")] ResponseFormat? ResponseFormat);

    private record ResponseFormat([property: JsonPropertyName("type")] string Type);

    private record Choice(Message Message);

    private record Response(List<Choice> Choices);

    /// Sends one completion request. Returns null on any failure.
    ///
    /// A failed narration must never take down the visualizer, so every error path here is a
    /// null rather than an exception: the caller shows the panel as unavailable and the user
    /// keeps stepping through their program.
    public async Task<string?> CompleteAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        bool jsonMode,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey)) return null;

        var request = new Request(
            model,
            new List<Message>
            {
                new("system", systemPrompt),
                new("user", userPrompt),
            },
            // Low but not zero: narration reads better with a little variety, and the cache
            // means a given step is only ever generated once anyway.
            Temperature: 0.2,
            MaxTokens: maxTokens,
            ResponseFormat: jsonMode ? new ResponseFormat("json_object") : null);

        using var message = new HttpRequestMessage(HttpMethod.Post, _options.BaseUrl)
        {
            Content = JsonContent.Create(request, options: Json),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        try
        {
            using var response = await _http.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // A 403 here almost always means the key's tier does not include this model,
                // which is a configuration problem rather than a transient one. Reporting it
                // distinctly lets the caller retry on a model the key can actually use instead
                // of silently offering no explanation at all.
                LastFailureWasModelAccess = response.StatusCode == System.Net.HttpStatusCode.Forbidden;
                return null;
            }

            LastFailureWasModelAccess = false;

            var body = await response.Content.ReadFromJsonAsync<Response>(Json, cancellationToken);
            var text = body?.Choices?.FirstOrDefault()?.Message?.Content;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
