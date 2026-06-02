namespace Diva.Infrastructure.Scheduler;

/// <summary>
/// Generates and validates HMAC-SHA256 signed feedback tokens for scheduler runs.
/// Tokens encode run/task/tenant context and are embedded in email notifications
/// and prompt template variables so users can submit feedback without logging in.
/// </summary>
public interface ISchedulerFeedbackTokenService
{
    /// <summary>
    /// Generates a signed feedback token for the given run.
    /// Returns a URL-safe string of the form: base64url(payload).base64url(hmac).
    /// </summary>
    string Generate(string runId, string taskId, int tenantId, string taskType);

    /// <summary>
    /// Validates the token signature and expiry.
    /// Returns null if the token is invalid, tampered, or expired.
    /// </summary>
    FeedbackTokenClaims? Validate(string token);
}

/// <summary>Decoded, validated claims from a scheduler feedback token.</summary>
public sealed record FeedbackTokenClaims(
    string RunId,
    string TaskId,
    int TenantId,
    string TaskType,
    DateTime ExpiresAt);
