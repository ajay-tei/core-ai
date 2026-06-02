namespace Diva.Core.Configuration;

public sealed class TaskSchedulerOptions
{
    public const string SectionName = "TaskScheduler";

    /// <summary>Master switch — set false to disable all scheduled execution without removing schedules.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>How often the polling loop checks for due tasks (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 30;

    /// <summary>Maximum tasks executing concurrently per host instance.</summary>
    public int MaxConcurrentRuns { get; set; } = 5;

    /// <summary>Maximum pending (queued) runs allowed per task before new due-fires are silently dropped.</summary>
    public int MaxQueuedRunsPerTask { get; set; } = 10;

    /// <summary>Maximum characters of agent response stored per run for history.</summary>
    public int MaxResponseStorageChars { get; set; } = 4000;

    /// <summary>
    /// A run that has been in "running" status for longer than this many minutes is
    /// considered stuck and will be automatically marked as failed so queued pending
    /// runs can proceed. Default 60 minutes. Set 0 to disable timeout recovery (startup
    /// recovery on service restart still applies).
    /// </summary>
    public int StuckRunTimeoutMinutes { get; set; } = 60;

    // ── Feedback link settings ────────────────────────────────────────────────

    /// <summary>
    /// Set false to disable feedback link generation entirely.
    /// When false, no tokens are generated and no feedback links appear in emails or prompts.
    /// </summary>
    public bool EnableFeedbackLinks { get; set; } = true;

    /// <summary>
    /// Base URL of the admin portal used to build the feedback link.
    /// Example: "https://portal.example.com" or "http://localhost:5173".
    /// When empty, feedback links are disabled even if EnableFeedbackLinks is true.
    /// </summary>
    public string FeedbackLinkBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// HMAC-SHA256 secret key used to sign feedback tokens.
    /// When empty, an ephemeral key is generated per startup (tokens will not survive restarts).
    /// Set a stable base64-encoded 32-byte value in production.
    /// </summary>
    public string FeedbackTokenSecret { get; set; } = string.Empty;

    /// <summary>Number of days until a generated feedback token expires. Default 30.</summary>
    public int FeedbackLinkExpiryDays { get; set; } = 30;
}
