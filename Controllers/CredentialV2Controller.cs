using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using BaseUsuarios.Api.Services.Email;
using BaseUsuarios.Api.Services.Pdf;
using Microsoft.AspNetCore.Mvc;

namespace BaseUsuarios.Api.Controllers;

[ApiController]
[Route("api/credential-v2")]
public sealed class CredentialV2Controller : ControllerBase
{
    public sealed class RegistrationReceiptV2Dto
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required] public string Nickname { get; set; } = "";
        [Required] public string Phone { get; set; } = "";
        public string? FullName { get; set; } = null;
        public string? PhotoBase64 { get; set; } = null; // Fotografía2 opcional (dataURL/base64)
    }

    private readonly IEmailSender _email;

    public CredentialV2Controller(IEmailSender email)
    {
        _email = email; // ya lo tienes en DI, no tocamos Program.cs
    }

    [HttpPost("registration-receipt")]
    public async Task<IActionResult> SendRegistrationReceipt([FromBody] RegistrationReceiptV2Dto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var fullName = string.IsNullOrWhiteSpace(dto.FullName) ? dto.Nickname : dto.FullName;

        // ⚙️ Genera PDF con el nuevo builder (sin tocar tu servicio anterior)
        var pdf = CredentialV2PdfBuilder.Build(
            fullName: fullName!,
            nickname: dto.Nickname,
            email: dto.Email,
            phone: dto.Phone,
            photoBase64: dto.PhotoBase64
        );

        var fileName = $"Credencial_{dto.Nickname}.pdf";
        var htmlBody = $@"
            <p>Hola <b>{System.Net.WebUtility.HtmlEncode(dto.Nickname)}</b>,</p>
            <p>Adjuntamos tu credencial.</p>
            <p>Saludos,<br/>Proyecto Final</p>";

        await _email.SendAsync(
            toEmail: dto.Email,
            subject: "Tu credencial de acceso",
            htmlBody: htmlBody,
            attachment: (fileName, pdf, "application/pdf"),
            ct: ct
        );

        return Ok(new { success = true, message = "Credencial enviada al correo (v2)." });
    }
}
