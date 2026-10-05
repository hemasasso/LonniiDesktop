using System.Net;
using System.Net.Mail;

namespace Lonnii.Api.Features.Registration;

/// <summary>Sends the one email registration needs: the confirmation code (an HTML message).</summary>
public interface IEmailSender
{
    /// <summary>False when no way of sending is configured, so registration can say so
    /// instead of silently accepting requests whose code nobody will ever receive.</summary>
    bool IsConfigured { get; }

    /// <summary>Throws <see cref="EmailSendException"/> when the message could not be sent.</summary>
    Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct);
}

public sealed class EmailSendException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Read from <c>Lonnii:Email</c> - on OCI, the environment variables <c>Lonnii__Email__Host</c>,
/// <c>__Port</c>, <c>__User</c>, <c>__Password</c>, <c>__From</c>.
///
/// <para>
/// Lonnii Business sends from <c>mail.privateemail.com</c> on port 465 (implicit SSL). This sender
/// uses .NET's SmtpClient, which only speaks STARTTLS - so use the same host on <b>port 587</b>
/// with <c>EnableSsl</c> true. Same mailbox, same account.
/// </para>
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Lonnii:Email";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }

    /// <summary>The name shown beside the address, as Lonnii Business does ("Lonnii" &lt;address&gt;).</summary>
    public string FromName { get; set; } = "Lonnii";
    public bool EnableSsl { get; set; } = true;
}

public sealed class SmtpEmailSender(EmailOptions options) : IEmailSender
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Host) && !string.IsNullOrWhiteSpace(options.From);

    public async Task SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            Timeout = 20_000,
        };

        if (!string.IsNullOrWhiteSpace(options.User))
            client.Credentials = new NetworkCredential(options.User, options.Password);

        using var message = new MailMessage(new MailAddress(options.From!, options.FromName), new MailAddress(to))
        {
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true,
        };

        try
        {
            await client.SendMailAsync(message, ct);
        }
        catch (Exception e) when (e is SmtpException or InvalidOperationException or FormatException or ArgumentException)
        {
            throw new EmailSendException("Impossible d'envoyer l'email de confirmation.", e);
        }
    }
}

/// <summary>
/// Development only: writes the email to the log instead of sending it. Never registered
/// outside the Development environment - in production, a code that only appears in a log
/// would let whoever reads the log confirm any address.
/// </summary>
public sealed class LogEmailSender(ILogger<LogEmailSender> logger) : IEmailSender
{
    public bool IsConfigured => true;

    public Task SendAsync(string to, string subject, string body, CancellationToken ct)
    {
        logger.LogWarning("EMAIL (développement) à {To} : {Subject}\n{Body}", to, subject, body);
        return Task.CompletedTask;
    }
}
