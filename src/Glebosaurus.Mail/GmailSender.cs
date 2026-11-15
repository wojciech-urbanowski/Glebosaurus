using System.Net.Sockets;
using Glebosaurus.Core.Mail;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Glebosaurus.Mail;

/// <summary>
/// Wysyłka przez smtp.gmail.com:465 (SSL) z hasłem aplikacji Gmail. Podejście i parametry jak w LibrusNotifier;
/// różnica: połączenie na każdą wysyłkę (maile są rzadkie), więc nie trzeba pilnować utrzymania sesji.
/// </summary>
public sealed class GmailSender(string senderEmail, string appPassword, string senderName = "Glebosaurus") : IMailGateway
{
    public const string Host = "smtp.gmail.com";
    public const int Port = 465;

    public async Task SendAsync(MailMessage message, CancellationToken cancellationToken)
    {
        var mime = BuildMessage(senderEmail, senderName, message);
        using var client = new SmtpClient { Timeout = 60_000 };
        try
        {
            await client.ConnectAsync(Host, Port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(senderEmail, appPassword, cancellationToken);
            await client.SendAsync(mime, cancellationToken);
        }
        catch (Exception e) when (IsTransportFailure(e))
        {
            throw new MailTransportException($"Błąd wysyłki przez {Host}: {e.Message}", e);
        }
        finally
        {
            try { if (client.IsConnected) await client.DisconnectAsync(true, CancellationToken.None); }
            catch (Exception e) when (IsTransportFailure(e)) { /* połączenie i tak jest porzucane */ }
        }
    }

    /// <summary>Wiadomość multipart/alternative (tekst i HTML), z załącznikami jako multipart/mixed.</summary>
    internal static MimeMessage BuildMessage(string senderEmail, string senderName, MailMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(new MailboxAddress(senderName, senderEmail));
        foreach (var address in message.To) mime.To.Add(MailboxAddress.Parse(address));
        mime.Subject = message.Subject;
        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        foreach (var attachment in message.Attachments ?? [])
            body.Attachments.Add(attachment.FileName, attachment.Content, MimeKit.ContentType.Parse(attachment.ContentType));
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private static bool IsTransportFailure(Exception e) =>
        e is IOException or SocketException or ProtocolException or ServiceNotConnectedException or ServiceNotAuthenticatedException
            or AuthenticationException or SslHandshakeException or TimeoutException;
}
