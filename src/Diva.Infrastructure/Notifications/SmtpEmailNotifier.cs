using System.Net;
using System.Net.Mail;
using Diva.Core.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.Notifications;

public sealed class SmtpEmailNotifier(
    IOptions<SmtpOptions> opts,
    ILogger<SmtpEmailNotifier> logger) : IEmailNotifier
{
    private readonly SmtpOptions _opts = opts.Value;

    public async Task SendAsync(IEnumerable<string> recipients, string subject, string body, CancellationToken ct)
    {
        if (!_opts.IsConfigured)
        {
            logger.LogWarning("SMTP not configured (Host is empty) — skipping notification: {Subject}", subject);
            return;
        }

        var list = recipients.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        if (list.Count == 0) return;

        using var client = new SmtpClient(_opts.Host, _opts.Port)
        {
            EnableSsl = _opts.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(_opts.Username, _opts.Password)
        };

        using var msg = new MailMessage
        {
            From = new MailAddress(_opts.DefaultFromAddress, _opts.DefaultFromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };
        foreach (var r in list) msg.To.Add(r);

        Exception? ex = null;
        try { await client.SendMailAsync(msg, ct); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
            logger.LogWarning(ex, "SMTP send failed: {Subject} → {Recipients}", subject, string.Join(", ", list));
        else
            logger.LogInformation("Email sent: {Subject} → {Recipients}", subject, string.Join(", ", list));
    }
}
