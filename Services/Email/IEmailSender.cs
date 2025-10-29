using System.Threading;
using System.Threading.Tasks;

namespace BaseUsuarios.Api.Services.Email;

public interface IEmailSender
{
    Task SendAsync(
        string toEmail,
        string subject,
        string htmlBody,
        (string fileName, byte[] content, string contentType)? attachment = null,
        CancellationToken ct = default
    );
}
