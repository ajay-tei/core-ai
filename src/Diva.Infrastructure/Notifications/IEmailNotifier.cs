namespace Diva.Infrastructure.Notifications;

/// <summary>
/// Sends HTML notification emails. Implementations must no-op silently
/// when the underlying transport is not configured.
/// </summary>
public interface IEmailNotifier
{
    Task SendAsync(IEnumerable<string> recipients, string subject, string body, CancellationToken ct);
}
