// BaseUsuarios.Api/Controllers/NotificationsController.cs
using System;
using System.Data;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BaseUsuarios.Api.Services.Email;
using BaseUsuarios.Api.Services.PdfHtml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    // ===== DTO original =====
    public sealed class RegistrationReceiptDto
    {
        [System.ComponentModel.DataAnnotations.Required, System.ComponentModel.DataAnnotations.EmailAddress]
        public string Email { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required]
        public string Nickname { get; set; } = string.Empty;

        [System.ComponentModel.DataAnnotations.Required]
        public string Phone { get; set; } = string.Empty;

        // Opcional
        public string? FullName { get; set; }

        /// <summary>dataURL (data:image/...;base64,xxx) o base64 puro de la foto (se usará si viene).</summary>
        public string? PhotoBase64 { get; set; }
    }

    private readonly IEmailSender _email;
    private readonly IHtmlPdfService _htmlPdf;
    private readonly IConfiguration _cfg;
    private readonly ILogger<NotificationsController> _log;

    public NotificationsController(IEmailSender email, IHtmlPdfService htmlPdf, IConfiguration cfg, ILogger<NotificationsController> log)
    {
        _email = email;
        _htmlPdf = htmlPdf;
        _cfg = cfg;
        _log = log;
    }

    private string Cs => _cfg.GetConnectionString("Default")!;

    // ==============================================================
    // Genera PDF (HTML→PDF con Puppeteer) y lo envía por correo.
    // Si PhotoBase64 no viene, se toma Fotografia2/Fotografia desde BD.
    // ==============================================================

    [HttpPost("registration-receipt-html")]
    public async Task<IActionResult> SendRegistrationReceiptHtml([FromBody] RegistrationReceiptDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        var fullName = string.IsNullOrWhiteSpace(dto.FullName) ? dto.Nickname : dto.FullName;

        // 1) Resolver foto: priorizar la que venga en DTO; si no, buscar en BD
        string? photoDataUrl = dto.PhotoBase64;
        if (string.IsNullOrWhiteSpace(photoDataUrl))
        {
            try
            {
                photoDataUrl = await ResolvePhotoFromDbAsDataUrlAsync(
                    identifierEmail: dto.Email,
                    identifierNickname: dto.Nickname,
                    identifierPhone: dto.Phone,
                    ct: ct
                );
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "No se pudo obtener la foto desde BD; se enviará sin foto.");
            }
        }

        // 2) Construir el HTML con tus datos + QR + foto (si la hay)
        var html = CredentialHtmlBuilder.Build(
            fullName: fullName!,
            nickname: dto.Nickname,
            email: dto.Email,
            phone: dto.Phone,
            photoDataUrl: photoDataUrl // <- ya puede venir del DTO o de la BD
        ); // El builder acepta dataURL/base64 y la incrusta en <img src="..."> :contentReference[oaicite:5]{index=5} :contentReference[oaicite:6]{index=6}

        // 3) Convertir HTML → PDF (Chromium)
        var pdfBytes = _htmlPdf.FromHtml(html); // :contentReference[oaicite:7]{index=7}

        // 4) Enviar email con adjunto
        var fileName = $"Credencial_{dto.Nickname}.pdf";
        var body = $@"
            <p>Hola <b>{WebUtility.HtmlEncode(dto.Nickname)}</b>,</p>
            <p>Adjuntamos tu credencial.</p>
            <p>Saludos,<br/>Proyecto Final</p>";

        await _email.SendAsync(
            toEmail: dto.Email,
            subject: "Tu credencial de acceso",
            htmlBody: body,
            attachment: (fileName, pdfBytes, "application/pdf"),
            ct: ct
        );

        return Ok(new { success = true, message = "Credencial enviada al correo." });
    }

    // Alias que mantiene tu ruta anterior
    [HttpPost("registration-receipt")]
    public Task<IActionResult> SendRegistrationReceipt([FromBody] RegistrationReceiptDto dto, CancellationToken ct)
        => SendRegistrationReceiptHtml(dto, ct);

    // ======================== Helper privado ========================
    // Busca primero Fotografia2 (y su MIME) y si está nula cae a Fotografia.
    // Identifica al usuario por Email/Nickname/Teléfono (lo que venga).
    private async Task<string?> ResolvePhotoFromDbAsDataUrlAsync(string identifierEmail, string identifierNickname, string identifierPhone, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync(ct);

        const string sql = @"
            SELECT
              Fotografia2,  Fotografia2Mime,
              Fotografia,   FotografiaMime
            FROM usuarios
            WHERE Email = @e OR Nickname = @n OR Telefono = @t
            ORDER BY UsuarioId DESC
            LIMIT 1;";

        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@e", identifierEmail?.Trim() ?? "");
        cmd.Parameters.AddWithValue("@n", identifierNickname?.Trim() ?? "");
        cmd.Parameters.AddWithValue("@t", identifierPhone?.Trim() ?? "");

        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await rd.ReadAsync(ct)) return null;

        byte[]? bytes = null;
        string? mime  = null;

        if (!rd.IsDBNull(rd.GetOrdinal("Fotografia2")))
        {
            bytes = ReadBlob(rd, "Fotografia2");
            mime  = rd.IsDBNull(rd.GetOrdinal("Fotografia2Mime")) ? null : rd.GetString(rd.GetOrdinal("Fotografia2Mime"));
        }
        else if (!rd.IsDBNull(rd.GetOrdinal("Fotografia")))
        {
            bytes = ReadBlob(rd, "Fotografia");
            mime  = rd.IsDBNull(rd.GetOrdinal("FotografiaMime")) ? null : rd.GetString(rd.GetOrdinal("FotografiaMime"));
        }

        if (bytes is not { Length: > 0 }) return null;

        var contentType = string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime!;
        return $"data:{contentType};base64,{Convert.ToBase64String(bytes)}";
    }

    private static byte[] ReadBlob(MySqlDataReader rd, string column)
    {
        var ord = rd.GetOrdinal(column);
        var len = rd.GetBytes(ord, 0, null, 0, 0);
        var buf = new byte[len];
        rd.GetBytes(ord, 0, buf, 0, (int)len);
        return buf;
    }
}
