using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Diva.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Scheduler;

/// <summary>
/// HMAC-SHA256 token service. Token format: base64url(json_payload).base64url(signature).
/// The service is registered as singleton — token generation and validation are pure crypto,
/// no DB access or tenant context required.
/// </summary>
public sealed class SchedulerFeedbackTokenService : ISchedulerFeedbackTokenService
{
    private readonly byte[] _secretKey;
    private readonly int _expiryDays;

    private sealed record TokenPayload(
        string RunId,
        string TaskId,
        int TenantId,
        string TaskType,
        long IssuedAt,   // Unix timestamp seconds
        long ExpiresAt); // Unix timestamp seconds

    public SchedulerFeedbackTokenService(
        IOptions<TaskSchedulerOptions> opts,
        ILogger<SchedulerFeedbackTokenService> logger)
    {
        var secret = opts.Value.FeedbackTokenSecret;
        _expiryDays = opts.Value.FeedbackLinkExpiryDays > 0 ? opts.Value.FeedbackLinkExpiryDays : 30;

        if (string.IsNullOrWhiteSpace(secret))
        {
            // Ephemeral key — tokens won't survive service restart (same behaviour as Credentials:MasterKey)
            _secretKey = RandomNumberGenerator.GetBytes(32);
            logger.LogWarning(
                "TaskScheduler:FeedbackTokenSecret is not configured. " +
                "An ephemeral key was generated — feedback tokens will not survive service restarts. " +
                "Set a stable base64-encoded 32-byte key in production.");
        }
        else
        {
            // Accept base64 or raw UTF-8 secret
            try { _secretKey = Convert.FromBase64String(secret); }
            catch { _secretKey = Encoding.UTF8.GetBytes(secret); }
        }
    }

    public string Generate(string runId, string taskId, int tenantId, string taskType)
    {
        var now = DateTimeOffset.UtcNow;
        var payload = new TokenPayload(
            runId, taskId, tenantId, taskType,
            now.ToUnixTimeSeconds(),
            now.AddDays(_expiryDays).ToUnixTimeSeconds());

        var payloadJson = JsonSerializer.Serialize(payload);
        var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);
        var payloadB64 = Base64UrlEncode(payloadBytes);

        var sig = Sign(payloadBytes);
        var sigB64 = Base64UrlEncode(sig);

        return $"{payloadB64}.{sigB64}";
    }

    public FeedbackTokenClaims? Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var parts = token.Split('.');
        if (parts.Length != 2) return null;

        byte[] payloadBytes;
        byte[] sigBytes;
        try
        {
            payloadBytes = Base64UrlDecode(parts[0]);
            sigBytes = Base64UrlDecode(parts[1]);
        }
        catch { return null; }

        // Constant-time compare to prevent timing attacks
        var expectedSig = Sign(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(sigBytes, expectedSig)) return null;

        TokenPayload? payload;
        try
        {
            var json = Encoding.UTF8.GetString(payloadBytes);
            payload = JsonSerializer.Deserialize<TokenPayload>(json);
        }
        catch { return null; }

        if (payload is null) return null;

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.ExpiresAt).UtcDateTime;
        if (expiresAt < DateTime.UtcNow) return null;

        return new FeedbackTokenClaims(
            payload.RunId,
            payload.TaskId,
            payload.TenantId,
            payload.TaskType,
            expiresAt);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private byte[] Sign(byte[] data)
    {
        using var hmac = new HMACSHA256(_secretKey);
        return hmac.ComputeHash(data);
    }

    private static string Base64UrlEncode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string encoded)
    {
        var s = encoded.Replace('-', '+').Replace('_', '/');
        var padding = (4 - s.Length % 4) % 4;
        s += new string('=', padding);
        return Convert.FromBase64String(s);
    }
}
