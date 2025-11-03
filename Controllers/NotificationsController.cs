// BaseUsuarios.Api/Controllers/NotificationsController.cs
using System;
using System.Data;
using System.Net;
using System.Net.Mail;                   // Validación de email con MailAddress
using System.Threading;
using System.Threading.Tasks;
using BaseUsuarios.Api.Services.Email;
using BaseUsuarios.Api.Services.Pdf;     // QuestPDF (no Chromium)
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Controllers;

[ApiController]
[Route("api/notifications")]
public sealed class NotificationsController : ControllerBase
{
    // DTO TODO OPCIONAL (evita 400 automáticos)
    public sealed class RegistrationReceiptDto
    {
        public long?   UserId     { get; set; }     // ← NUEVO: nos permite resolver por Id
        public string? Email      { get; set; }
        public string? Nickname   { get; set; }
        public string? Phone      { get; set; }
        public string? FullName   { get; set; }
        /// dataURL (data:image/...;base64,xxx) o base64 puro
        public string? PhotoBase64 { get; set; }
    }

    private readonly IEmailSender _email;
    private readonly IConfiguration _cfg;
    private readonly ILogger<NotificationsController> _log;

    public NotificationsController(IEmailSender email, IConfiguration cfg, ILogger<NotificationsController> log)
    { _email = email; _cfg = cfg; _log = log; }

    private string Cs => _cfg.GetConnectionString("Default")!;

    [HttpPost("registration-receipt")]
    [Produces("application/json")]
    public async Task<IActionResult> SendRegistrationReceipt([FromBody] RegistrationReceiptDto? dto, CancellationToken ct)
    {
        if (dto is null)
            return BadRequest(new { success = false, message = "Body vacío o JSON inválido." });

        var userId   = dto.UserId;
        var emailRaw = (dto.Email ?? string.Empty).Trim();
        var nickname = (dto.Nickname ?? string.Empty).Trim();
        var phone    = (dto.Phone ?? string.Empty).Trim();
        var fullName = string.IsNullOrWhiteSpace(dto.FullName) ? nickname : dto.FullName!.Trim();

        // 1) Resolver EMAIL con prioridad:
        //    a) UserId → Email (robusto justo después de registrar)
        //    b) Email directo si es válido
        //    c) Nickname / Phone normalizado (por si no tienes el Id)
        string? email = null;

        // a) por Id
        if (userId is not null)
            email = await TryResolveEmailByIdAsync(userId.Value, ct);

        // b) si no salió por Id, usa el aportado si es válido
        if (string.IsNullOrWhiteSpace(email) && IsValidEmail(emailRaw))
            email = emailRaw;

        // c) por nickname/phone (con normalización)
        if (string.IsNullOrWhiteSpace(email))
            email = await TryResolveEmailByNickOrPhoneAsync(nickname, phone, ct);

        if (string.IsNullOrWhiteSpace(email))
            return BadRequest(new { success = false, message = "No se pudo resolver el Email (envía email válido o incluye userId / nickname / phone registrados)." });

        // 2) Foto: prioriza la que manda el front; si no, BD (ahora también soporta UserId)
        string? photoDataUrl = string.IsNullOrWhiteSpace(dto.PhotoBase64)
            ? await ResolvePhotoFromDbAsDataUrlAsync(userId, email, nickname, phone, ct)
            : dto.PhotoBase64;

        // 3) Generar PDF (QuestPDF, sin Chromium)
        byte[]? pdfBytes = null;
        try
        {
            var displayName = string.IsNullOrWhiteSpace(fullName)
                ? (string.IsNullOrWhiteSpace(nickname) ? email : nickname)
                : fullName!;

            pdfBytes = CredentialV2PdfBuilder.Build(
                fullName:    displayName,
                nickname:    nickname,
                email:       email,
                phone:       phone,
                photoBase64: photoDataUrl
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "No se pudo generar PDF; se enviará sin adjunto.");
        }

        // 4) Enviar correo
        var fileName = $"Credencial_{(string.IsNullOrWhiteSpace(nickname) ? "usuario" : nickname)}.pdf";
        var body = $@"
            <p>Hola <b>{WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(nickname) ? email : nickname)}</b>,</p>
            <p>Adjuntamos tu credencial.</p>
            <p>Saludos,<br/>Proyecto Final</p>";

        await _email.SendAsync(
            toEmail:  email,
            subject:  "Tu credencial de acceso",
            htmlBody: body,
            attachment: (pdfBytes is null) ? null : (fileName, pdfBytes, "application/pdf"),
            ct: ct
        );

        Response.Headers["X-Receipt-Version"] = "v20251103-receipt-by-id-phone-norm";
        return Ok(new { success = true, message = "Credencial enviada al correo." });
    }

    // ---------------- Validación de email ----------------
    private static bool IsValidEmail(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        try
        {
            var addr = new MailAddress(s);
            return string.Equals(addr.Address, s, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ---------------- Resolver email por UsuarioId ----------------
    private async Task<string?> TryResolveEmailByIdAsync(long userId, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync(ct);

        const string sql = @"SELECT Email FROM usuarios WHERE UsuarioId = @id LIMIT 1;";
        await using var cmd = new MySqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", userId);

        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    }

    // ----------- Resolver email por Nick/Phone (normalizado) -----------
    private async Task<string?> TryResolveEmailByNickOrPhoneAsync(string nickname, string phone, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(nickname) && string.IsNullOrWhiteSpace(phone))
            return null;

        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync(ct);

        // Normalizamos teléfono en ambos lados: quitamos espacios, -, +, (, )
        const string normPhoneExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Telefono,' ',''),'-',''),'+',''),'(',''),')','')";
        const string normParamExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@t,' ',''),'-',''),'+',''),'(',''),')','')";

        const string sql = $@"
SELECT Email
FROM usuarios
WHERE
  (@n IS NOT NULL AND CONVERT(Nickname USING utf8mb4) COLLATE utf8mb4_0900_ai_ci = CONVERT(@n USING utf8mb4) COLLATE utf8mb4_0900_ai_ci)
  OR
  (@t IS NOT NULL AND CONVERT({normPhoneExpr} USING utf8mb4) COLLATE utf8mb4_0900_ai_ci = CONVERT({normParamExpr} USING utf8mb4) COLLATE utf8mb4_0900_ai_ci)
ORDER BY UsuarioId DESC
LIMIT 1;";

        await using var cmd = new MySqlCommand(sql, conn);
        object DbNullIf(string s) => string.IsNullOrWhiteSpace(s) ? (object)DBNull.Value : s.Trim();
        cmd.Parameters.AddWithValue("@n", DbNullIf(nickname));
        cmd.Parameters.AddWithValue("@t", DbNullIf(phone));

        var obj = await cmd.ExecuteScalarAsync(ct);
        return obj is string s && !string.IsNullOrWhiteSpace(s) ? s.Trim() : null;
    }

    // ---- Foto desde BD (ahora también soporta UserId) ----
    private async Task<string?> ResolvePhotoFromDbAsDataUrlAsync(
        long? id, string identifierEmail, string identifierNickname, string identifierPhone, CancellationToken ct)
    {
        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync(ct);

        const string normPhoneExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(Telefono,' ',''),'-',''),'+',''),'(',''),')','')";
        const string normParamExpr = "REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(@t,' ',''),'-',''),'+',''),'(',''),')','')";

        const string sql = $@"
SELECT Fotografia2, Fotografia2Mime, Fotografia, FotografiaMime
FROM usuarios
WHERE
  (@id IS NOT NULL AND UsuarioId = @id)
  OR
  (@e  IS NOT NULL AND CONVERT(Email    USING utf8mb4) COLLATE utf8mb4_0900_ai_ci = CONVERT(@e USING utf8mb4)  COLLATE utf8mb4_0900_ai_ci)
  OR
  (@n  IS NOT NULL AND CONVERT(Nickname USING utf8mb4) COLLATE utf8mb4_0900_ai_ci = CONVERT(@n USING utf8mb4)  COLLATE utf8mb4_0900_ai_ci)
  OR
  (@t  IS NOT NULL AND CONVERT({normPhoneExpr} USING utf8mb4) COLLATE utf8mb4_0900_ai_ci = CONVERT({normParamExpr} USING utf8mb4)  COLLATE utf8mb4_0900_ai_ci)
ORDER BY UsuarioId DESC
LIMIT 1;";

        await using var cmd = new MySqlCommand(sql, conn);
        object DbNullIf(string s) => string.IsNullOrWhiteSpace(s) ? (object)DBNull.Value : s.Trim();
        cmd.Parameters.AddWithValue("@id", id is null ? (object)DBNull.Value : id.Value);
        cmd.Parameters.AddWithValue("@e",  DbNullIf(identifierEmail));
        cmd.Parameters.AddWithValue("@n",  DbNullIf(identifierNickname));
        cmd.Parameters.AddWithValue("@t",  DbNullIf(identifierPhone));

        await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await rd.ReadAsync(ct)) return null;

        byte[]? bytes = null; string? mime = null;
        if (!rd.IsDBNull(rd.GetOrdinal("Fotografia2")))
        { bytes = ReadBlob(rd, "Fotografia2"); mime = rd.IsDBNull(rd.GetOrdinal("Fotografia2Mime")) ? null : rd.GetString(rd.GetOrdinal("Fotografia2Mime")); }
        else if (!rd.IsDBNull(rd.GetOrdinal("Fotografia")))
        { bytes = ReadBlob(rd, "Fotografia");  mime = rd.IsDBNull(rd.GetOrdinal("FotografiaMime")) ? null : rd.GetString(rd.GetOrdinal("FotografiaMime")); }

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
