// BaseUsuarios.Api/Controllers/CredentialV3Controller.cs
using System;
using System.Data;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using BaseUsuarios.Api.Models;
using BaseUsuarios.Api.Services.Email;
using BaseUsuarios.Api.Services.PdfHtml;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Controllers;

[ApiController]
[Route("api/credential-v3")] // => /api/credential-v3/registration-receipt-dbphoto
public class CredentialV3Controller : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<CredentialV3Controller> _log;
    private readonly IEmailSender _email;
    private readonly IHtmlPdfService _htmlPdf;

    public CredentialV3Controller(
        IConfiguration cfg,
        ILogger<CredentialV3Controller> log,
        IEmailSender email,
        IHtmlPdfService htmlPdf)
    {
        _cfg = cfg; _log = log; _email = email; _htmlPdf = htmlPdf;
    }

    private string Cs => _cfg.GetConnectionString("Default")!;

    [HttpPost("registration-receipt-dbphoto")]
    public async Task<IActionResult> SendRegistrationReceiptDbPhoto(
        [FromBody] RegistrationReceiptDbPhotoDto dto,
        CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return ValidationProblem(ModelState);

        try
        {
            // 1) Resolver UsuarioId
            long? userId = dto.UserId ?? await FindUserIdAsync(dto.Credential, ct);
            if (userId is null)
                return NotFound(new { message = "No se encontró el usuario (proporciona UserId o Credential)." });

            // 2) Cargar foto2 (o foto1 si no hay) y MIME
            var (photoDataUrl, resolvedNickname, resolvedPhone, resolvedFullName) =
                await GetPhoto2AndUserDataAsync(userId.Value, dto, ct);

            // 3) Armar HTML con tu plantilla existente (inserta la foto si hay)
            var fullName = !string.IsNullOrWhiteSpace(dto.FullName)
                ? dto.FullName!
                : (!string.IsNullOrWhiteSpace(resolvedFullName) ? resolvedFullName! :
                   !string.IsNullOrWhiteSpace(dto.Nickname) ? dto.Nickname! : (resolvedNickname ?? "Usuario"));

            var nickname = !string.IsNullOrWhiteSpace(dto.Nickname) ? dto.Nickname! : (resolvedNickname ?? "user");
            var phone    = !string.IsNullOrWhiteSpace(dto.Phone)    ? dto.Phone!    : (resolvedPhone ?? "");

            // Usa CredentialHtmlBuilder (acepta dataURL o base64 puro)
            var html = BaseUsuarios.Api.Services.PdfHtml.CredentialHtmlBuilder.Build(
                fullName: fullName,
                nickname: nickname,
                email: dto.Email,
                phone: phone,
                photoDataUrl: photoDataUrl // <— Fotografia2 (o fallback)
            );

            // 4) HTML → PDF con Chromium (IHtmlPdfService)
            var pdfBytes = _htmlPdf.FromHtml(html);

            // 5) Enviar correo con adjunto
            var fileName = $"Credencial_{nickname}.pdf";
            var body = $@"
                <p>Hola <b>{WebUtility.HtmlEncode(nickname)}</b>,</p>
                <p>Adjuntamos tu credencial con tu fotografía personalizada.</p>
                <p>Saludos,<br/>Proyecto Final</p>";

            await _email.SendAsync(
                toEmail: dto.Email,
                subject: "Tu credencial con fotografía",
                htmlBody: body,
                attachment: (fileName, pdfBytes, "application/pdf"),
                ct: ct
            );

            return Ok(new { success = true, message = "Credencial enviada al correo (foto desde BD)." });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error generando/mandando credencial con foto de BD");
            return StatusCode(500, new { message = ex.Message });
        }
    }

    // Busca UsuarioId por email/teléfono/nickname (Credential)
    private async Task<long?> FindUserIdAsync(string? credential, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(credential)) return null;
        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync(ct);

        const string sql = @"
            SELECT UsuarioId
            FROM usuarios
            WHERE Email = @id OR Telefono = @id OR Nickname = @id
            ORDER BY UsuarioId DESC
            LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", credential.Trim());

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj == null || obj == DBNull.Value) return null;
        return Convert.ToInt64(obj);
    }

    // Lee Fotografia2 (+Mime) o cae a Fotografia, y trae nickname/phone/name
    private async Task<(string? photoDataUrl, string? nickname, string? phone, string? fullName)>
        GetPhoto2AndUserDataAsync(long userId, RegistrationReceiptDbPhotoDto dto, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync(ct);

        const string sql = @"
            SELECT
              Nickname,
              Telefono,
              CONCAT(TRIM(COALESCE(Nombre,'')), ' ', TRIM(COALESCE(Apellido,''))) AS FullName, -- opcional si tienes columnas
              Fotografia2,  Fotografia2Mime,
              Fotografia,   FotografiaMime
            FROM usuarios
            WHERE UsuarioId = @id
            LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", userId);

        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await rd.ReadAsync(ct))
            return (null, null, null, null);

        string? nickname = rd.IsDBNull(rd.GetOrdinal("Nickname")) ? null : rd.GetString(rd.GetOrdinal("Nickname"));
        string? phone    = rd.IsDBNull(rd.GetOrdinal("Telefono")) ? null : rd.GetString(rd.GetOrdinal("Telefono"));
        string? fullName = rd.IsDBNull(rd.GetOrdinal("FullName")) ? null : rd.GetString(rd.GetOrdinal("FullName"));

        // Preferimos Fotografia2
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

        var photoDataUrl = (bytes is { Length: > 0 })
            ? $"data:{(string.IsNullOrWhiteSpace(mime) ? "image/jpeg" : mime)};base64,{Convert.ToBase64String(bytes)}"
            : null;

        return (photoDataUrl, nickname, phone, fullName);
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
