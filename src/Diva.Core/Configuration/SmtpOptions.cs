namespace Diva.Core.Configuration;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    /// <summary>From address used for all outbound notification emails.</summary>
    public string DefaultFromAddress { get; set; } = "noreply@diva.local";

    /// <summary>Display name shown in the From field.</summary>
    public string DefaultFromName { get; set; } = "Diva AI Platform";

    /// <summary>False when Host is empty — disables all email sending without throwing.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);
}
