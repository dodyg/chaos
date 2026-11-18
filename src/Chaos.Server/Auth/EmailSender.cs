using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Auth;

/// <summary>Sends one plain-text email. Implementations never log the body (it holds a sign-in code).</summary>
public interface IEmailSender
{
    Task SendAsync(string to, string subject, string body, CancellationToken ct);
}

/// <summary>The SMTP adapter. Development points it at Mailpit.</summary>
public sealed class SmtpEmailSender(IOptions<SmtpOptions> options) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken ct)
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
        await client.SendMailAsync(message, ct);
    }
}
