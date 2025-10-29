using System.Threading;
using System.Threading.Tasks;
using BaseUsuarios.Api.Config;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BaseUsuarios.Api.Services.Email;

public sealed class MailKitEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    public MailKitEmailSender(IOptions<SmtpOptions> opt) => _opt = opt.Value;

    public async Task SendAsync(
        string toEmail, string subject, string htmlBody,
        (string fileName, byte[] content, string contentType)? attachment = null,
        CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(_opt.FromName, _opt.FromEmail));
        msg.To.Add(MailboxAddress.Parse(toEmail));
        msg.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody };

        if (attachment.HasValue)
        {
            builder.Attachments.Add(
                attachment.Value.fileName,
                attachment.Value.content,
                ContentType.Parse(attachment.Value.contentType)
            );
        }

        msg.Body = builder.ToMessageBody();

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(_opt.Host, _opt.Port,
            _opt.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.Auto, ct);
        await smtp.AuthenticateAsync(_opt.User, _opt.Password, ct);
        await smtp.SendAsync(msg, ct);
        await smtp.DisconnectAsync(true, ct);
    }
}
