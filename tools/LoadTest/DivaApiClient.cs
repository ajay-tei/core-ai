using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Diva.LoadTest;

/// <summary>Thin HTTP client for the Diva agent API — talks over the wire exactly like the admin portal does.</summary>
public sealed class DivaApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    public DivaApiClient(LoadTestOptions opts)
    {
        var handler = new SocketsHttpHandler
        {
            // The test harness must not become the bottleneck: allow far more concurrent
            // connections per target host than any realistic virtual-user count in this tool.
            MaxConnectionsPerServer = 2000,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _http = new HttpClient(handler)
        {
            // Trailing slash + no leading slash on relative paths below: HttpClient replaces the
            // ENTIRE base path (not just appends) when a relative URI starts with '/', which would
            // silently drop any reverse-proxy path prefix (e.g. "/beta/tei-ai-prod").
            BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/"),
            Timeout = Timeout.InfiniteTimeSpan, // per-request timeout applied via CancellationToken instead
        };
        if (!string.IsNullOrWhiteSpace(opts.ApiKey))
            _http.DefaultRequestHeaders.Add("X-API-Key", opts.ApiKey);
    }

    public async Task<List<AgentSummaryDto>> ListAgentsAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync("api/agents", ct);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<AgentSummaryDto>>(json, JsonOpts) ?? [];
    }

    private static StringContent BuildBody(string agentQuery, string? sessionId) => new(
        JsonSerializer.Serialize(new
        {
            query = agentQuery,
            sessionId,
        }),
        Encoding.UTF8, "application/json");

    /// <summary>POST /api/agents/{id}/invoke — buffered, returns once the full response is ready.</summary>
    public async Task<(RequestResult Result, string? SessionId)> InvokeAsync(string agentId, string query, string? sessionId, TimeSpan timeout, int bucketSecond, int activeUsers)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var resp = await _http.PostAsync($"api/agents/{agentId}/invoke", BuildBody(query, sessionId), cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            sw.Stop();
            if (!resp.IsSuccessStatusCode)
                return (Fail(startedAt, sw, bucketSecond, activeUsers, (int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {SummarizeErrorBody(body)}"), null);

            string? returnedSessionId = null;
            try { returnedSessionId = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts).GetProperty("sessionId").GetString(); }
            catch (Exception) { /* best-effort — session chaining is a convenience, not correctness-critical */ }

            var result = new RequestResult
            {
                StartedAtUtc = startedAt,
                TotalMs = sw.Elapsed.TotalMilliseconds,
                Success = true,
                HttpStatus = (int)resp.StatusCode,
                BucketSecond = bucketSecond,
                ActiveUsersAtStart = activeUsers,
            };
            return (result, returnedSessionId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (Fail(startedAt, sw, bucketSecond, activeUsers, null, Classify(ex)), null);
        }
    }

    /// <summary>POST /api/agents/{id}/invoke/stream — parses the SSE event timeline for detailed timing.</summary>
    public async Task<(RequestResult Result, string? SessionId)> InvokeStreamAsync(string agentId, string query, string? sessionId, TimeSpan timeout, int bucketSecond, int activeUsers)
    {
        var sw = Stopwatch.StartNew();
        var startedAt = DateTime.UtcNow;
        using var cts = new CancellationTokenSource(timeout);

        double? ttfb = null, ttft = null, ttftool = null, ttdone = null;
        var toolCalls = 0;
        var iterations = 0;
        var verificationBlocked = false;
        string? errorMessage = null;
        string? returnedSessionId = null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"api/agents/{agentId}/invoke/stream")
            {
                Content = BuildBody(query, sessionId),
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                sw.Stop();
                return (Fail(startedAt, sw, bucketSecond, activeUsers, (int)resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {SummarizeErrorBody(body)}"), null);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var reader = new StreamReader(stream);
            var sawDone = false;

            while (await reader.ReadLineAsync(cts.Token) is { } line)
            {
                if (line.Length == 0 || !line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                ttfb ??= sw.Elapsed.TotalMilliseconds;

                AgentStreamChunkDto? chunk;
                try { chunk = JsonSerializer.Deserialize<AgentStreamChunkDto>(line["data: ".Length..], JsonOpts); }
                catch (JsonException) { continue; } // malformed/partial line — skip rather than fail the whole request

                if (chunk is null) continue;
                switch (chunk.Type)
                {
                    case "thinking" or "text_delta":
                        ttft ??= sw.Elapsed.TotalMilliseconds;
                        break;
                    case "tool_call":
                        ttftool ??= sw.Elapsed.TotalMilliseconds;
                        toolCalls++;
                        break;
                    case "iteration_start":
                        iterations++;
                        break;
                    case "verification":
                        verificationBlocked = chunk.Verification?.WasBlocked ?? false;
                        break;
                    case "final_response":
                        returnedSessionId = chunk.SessionId ?? returnedSessionId;
                        break;
                    case "error":
                        errorMessage = chunk.ErrorMessage ?? "unspecified stream error";
                        break;
                    case "done":
                        ttdone = sw.Elapsed.TotalMilliseconds;
                        sawDone = true;
                        break;
                }
            }
            sw.Stop();

            if (errorMessage is not null)
                return (Fail(startedAt, sw, bucketSecond, activeUsers, (int)resp.StatusCode, errorMessage), null);
            if (!sawDone)
                return (Fail(startedAt, sw, bucketSecond, activeUsers, (int)resp.StatusCode, "stream ended without a 'done' event"), null);

            var result = new RequestResult
            {
                StartedAtUtc = startedAt,
                TotalMs = sw.Elapsed.TotalMilliseconds,
                Success = true,
                HttpStatus = (int)resp.StatusCode,
                TimeToFirstByteMs = ttfb,
                TimeToFirstTokenMs = ttft,
                TimeToFirstToolCallMs = ttftool,
                TimeToDoneMs = ttdone,
                ToolCallCount = toolCalls,
                IterationCount = iterations,
                VerificationBlocked = verificationBlocked,
                BucketSecond = bucketSecond,
                ActiveUsersAtStart = activeUsers,
            };
            return (result, returnedSessionId);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (Fail(startedAt, sw, bucketSecond, activeUsers, null, Classify(ex)), null);
        }
    }

    private static RequestResult Fail(DateTime startedAt, Stopwatch sw, int bucketSecond, int activeUsers, int? httpStatus, string error) => new()
    {
        StartedAtUtc = startedAt,
        TotalMs = sw.Elapsed.TotalMilliseconds,
        Success = false,
        HttpStatus = httpStatus,
        Error = error,
        BucketSecond = bucketSecond,
        ActiveUsersAtStart = activeUsers,
    };

    private static string Classify(Exception ex) => ex switch
    {
        OperationCanceledException => "client timeout",
        HttpRequestException hre => $"connection error: {hre.Message}",
        _ => ex.GetType().Name + ": " + ex.Message,
    };

    /// <summary>
    /// Extracts a stable, groupable message from an error response body. ASP.NET Core's default
    /// ProblemDetails JSON includes a per-request "traceId", which would otherwise defeat the
    /// error-breakdown grouping in the report (every 404 would look like a distinct error).
    /// </summary>
    private static string SummarizeErrorBody(string body)
    {
        try
        {
            var el = JsonSerializer.Deserialize<JsonElement>(body, JsonOpts);
            if (el.TryGetProperty("title", out var title)) return title.GetString() ?? Truncate(body);
            if (el.TryGetProperty("error", out var error)) return error.GetString() ?? Truncate(body);
        }
        catch (JsonException) { /* not JSON — fall through to raw truncation */ }
        return Truncate(body);
    }

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
