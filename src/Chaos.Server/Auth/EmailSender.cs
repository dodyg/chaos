using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Auth;

/// <summary>Sends one plain-text email. Implementations never log the body (it holds a sign-in code).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
    Task SendHtmlAsync(string to, string subject, string text, string html, CancellationToken ct) =>
        SendAsync(to, subject, text, ct);
}

/// <summary>The SMTP adapter. Development points it at Mailpit.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
        => await SendCoreAsync(to, subject, body, null, ct);

    public async Task SendHtmlAsync(string to, string subject, string text, string html, CancellationToken ct)
        => await SendCoreAsync(to, subject, text, html, ct);

    private async Task SendCoreAsync(string to, string subject, string body, string? html, CancellationToken ct)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            throw new InvalidOperationException("Smtp:Host is not set, so email cannot be sent.");
        }

        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.EnableSsl };
        if (!string.IsNullOrEmpty(settings.Username))
        {
            client.Credentials = new NetworkCredential(settings.Username, settings.Password);
        }

        using var message = new MailMessage(new MailAddress(settings.From), new MailAddress(to)) { Subject = subject, Body = body };
        if (html is not null)
        {
            message.AlternateViews.Add(System.Net.Mail.AlternateView.CreateAlternateViewFromString(html, null, "text/html"));
        }
        await client.SendMailAsync(message, ct);
    }
}
