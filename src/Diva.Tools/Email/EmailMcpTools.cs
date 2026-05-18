using Diva.Infrastructure.Notifications;
using Diva.Tools.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace Diva.Tools.Email;

[McpServerToolType]
public sealed class EmailMcpTools(IHttpContextAccessor http) : IDivaMcpToolType
{
    private IEmailNotifier? Notifier =>
        http.HttpContext?.RequestServices.GetService<IEmailNotifier>();

    [McpServerTool(Name = "send_email"),
     Description("Sends an HTML email to one or more recipients. Use for status reports, alerts, and notifications.")]
    public async Task<string> SendEmailAsync(
        [Description("Comma-separated list of recipient email addresses.")] string to,
        [Description("Email subject line.")] string subject,
        [Description("HTML body of the email.")] string body_html,
        CancellationToken ct = default)
    {
        var notifier = Notifier;
        if (notifier is null)
            return """{"error":"Email service is not registered on this host."}""";

        var recipients = to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (recipients.Length == 0)
            return """{"error":"No valid recipient addresses provided."}""";

        Exception? ex = null;
        try { await notifier.SendAsync(recipients, subject, body_html, ct); }
        catch (Exception e) { ex = e; }

        if (ex is not null)
            return $$$"""{"error":"{{{ex.Message.Replace("\"", "\\\"")}}}"}""";

        return $"sent to {string.Join(", ", recipients)}";
    }
}
