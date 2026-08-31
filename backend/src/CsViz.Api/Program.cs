using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using CsViz.Ai;
using CsViz.Analysis;
using CsViz.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<Compiler>();
builder.Services.AddSingleton<TraceCache>();

// AI is optional everywhere. The service is always registered so the endpoints exist and can
// report why they are unavailable; without a key it simply never calls out.
builder.Services.AddSingleton(AiOptions.FromEnvironment());
builder.Services.AddHttpClient<MistralClient>();
builder.Services.AddSingleton<AiService>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod());
});

var app = builder.Build();

app.UseCors();

// The wire format is camelCase everywhere; declared once so no endpoint can serialise
// differently from another.
var json = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
};

const int MaxSourceBytes = 102_400;

app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

// The supported-language boundary, served so the UI can show it rather than letting users
// discover it by hitting it.
app.MapGet("/api/support", () => Results.Json(CsViz.Core.Bridge.Supported.Manifest, json));

app.MapPost("/api/trace", async (HttpRequest request, Compiler compiler, TraceCache cache) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    // Accept either {"source": "...", "stdin": "..."} or a raw C# body, so curl-style callers
    // keep working while the frontend can send stdin.
    string source = body;
    string? stdin = null;

    if ((request.ContentType ?? "").Contains("json", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("source", out var srcEl)) source = srcEl.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("stdin", out var inEl)) stdin = inEl.GetString();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Request body is not valid JSON." });
        }
    }

    if (source.Length > MaxSourceBytes)
        return Results.BadRequest(new { error = $"Source exceeds the {MaxSourceBytes / 1024}KB limit." });

    var trace = cache.GetOrRun(source, stdin, () => TraceRunner.Run(compiler, source, new TraceRunner.Options(Stdin: stdin)).Trace);
    return Results.Json(trace, json);
});

// Serves a previously traced program by its hash, which is what makes /t/{hash} permalinks
// shareable without the recipient needing the source.
app.MapGet("/api/trace/{hash}", (string hash, TraceCache cache) =>
{
    var trace = cache.TryGet(hash);
    return trace == null
        ? Results.NotFound(new { error = "No trace with that hash. Run the program first." })
        : Results.Json(trace, json);
});

// ---------------------------------------------------------------------------------------
// AI endpoints
//
// Every one of these can return 503. The frontend treats that as "hide the panel", never as
// an error: the visualizer has to be completely usable with no AI at all, which is also what
// makes it safe to ship without a key.
// ---------------------------------------------------------------------------------------

app.MapGet("/api/ai/status", (AiService ai) => Results.Json(ai.Status(), json));

app.MapPost("/api/ai/narrate", async (NarrateRequest request, HttpContext context, Compiler compiler, TraceCache cache, AiService ai, CancellationToken cancellationToken) =>
{
    if (!ai.Status().Available)
        return Results.Json(new { error = ai.Status().Reason }, json, statusCode: 503);

    if (!ai.AllowRequest(ClientId(context)))
        return Results.Json(new { error = "Too many requests. Try again in a minute." }, json, statusCode: 429);

    // The trace is re-derived from the cache rather than accepted from the client: a narration
    // request that carried its own trace would let a caller put arbitrary text in front of the
    // model while claiming it came from the interpreter.
    var trace = cache.TryGet(request.SourceHash);
    if (trace == null)
        return Results.Json(new { error = "Unknown trace. Run the program first." }, json, statusCode: 404);

    var narration = await ai.NarrateAsync(trace, request.StepIndex, cancellationToken);
    return narration == null
        ? Results.Json(new { error = "Narration is unavailable right now." }, json, statusCode: 503)
        : Results.Json(narration, json);
});

app.MapPost("/api/ai/explain", async (ExplainRequest request, HttpContext context, TraceCache cache, AiService ai, CancellationToken cancellationToken) =>
{
    if (!ai.Status().Available)
        return Results.Json(new { error = ai.Status().Reason }, json, statusCode: 503);

    if (!ai.AllowRequest(ClientId(context)))
        return Results.Json(new { error = "Too many requests. Try again in a minute." }, json, statusCode: 429);

    var trace = cache.TryGet(request.SourceHash);
    if (trace == null)
        return Results.Json(new { error = "Unknown trace. Run the program first." }, json, statusCode: 404);

    var explanation = await ai.ExplainAsync(trace, cancellationToken);
    return explanation == null
        ? Results.Json(new { error = "The explainer is unavailable right now." }, json, statusCode: 503)
        : Results.Json(explanation, json);
});

app.Run();

/// Identifies a caller for rate limiting.
///
/// Behind a proxy the socket address is the proxy's, so the forwarded header is preferred when
/// present. This is spoofable by a determined caller, which is why it is not the only defence:
/// the durable daily budget bounds the damage regardless of how many identities one caller
/// invents.
static string ClientId(HttpContext context)
{
    var forwarded = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwarded))
    {
        return forwarded.Split(',')[0].Trim();
    }
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

public record NarrateRequest(string SourceHash, int StepIndex);

public record ExplainRequest(string SourceHash);

/// Bounded in-memory trace cache keyed by SHA256(source + stdin).
///
/// Identical code returns instantly, and the same key powers shareable permalinks. Bounded
/// because an unbounded dictionary on a public endpoint is a memory-exhaustion vector: every
/// distinct program a visitor submits would be retained forever.
public class TraceCache
{
    private const int MaxEntries = 256;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TraceDto> _entries = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _insertionOrder = new();

    public TraceDto? TryGet(string hash) => _entries.TryGetValue(hash, out var t) ? t : null;

    public TraceDto GetOrRun(string source, string? stdin, Func<TraceDto> run)
    {
        var key = TraceRunner.HashOf(source, stdin);
        if (_entries.TryGetValue(key, out var cached)) return cached;

        var trace = run();

        // Everything except a compile error is cached. A runtime failure is exactly what the
        // explainer is asked about, so not retaining those made the explain endpoint
        // permanently unable to find the traces it exists to explain. A compile error is left
        // out because it never produced a trace worth keeping and is cheap to reproduce.
        if (trace.Status != "compile_error" && _entries.TryAdd(key, trace))
        {
            _insertionOrder.Enqueue(key);
            while (_insertionOrder.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
            {
                _entries.TryRemove(oldest, out _);
            }
        }

        return trace;
    }
}
