// BaseUsuarios.Api/Controllers/PasswordResetController.cs
using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Security.Cryptography;
using BaseUsuarios.Api.Utils;
using BaseUsuarios.Api.Services.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Controllers;

[ApiController]
[Route("api/password")]
public sealed class PasswordResetController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<PasswordResetController> _log;
    private readonly IEmailSender _email;
    public PasswordResetController(IConfiguration cfg, ILogger<PasswordResetController> log, IEmailSender email)
    { _cfg = cfg; _log = log; _email = email; }

    string Cs => _cfg.GetConnectionString("Default")!;
    string ResetSecret => _cfg["App:ResetSecret"] ?? throw new InvalidOperationException("App:ResetSecret faltante");
    string ResetUrlBase => _cfg["App:ResetUrlBase"] ?? "http://localhost:5173/reset-password";

    // DTOs
    public sealed class ForgotDto { public string? Credential { get; set; } }
    public sealed class ResetDto { public string? Token { get; set; } public string? NewPassword { get; set; } }

    // POST /api/password/forgot  (sin revelar existencia)
    [HttpPost("forgot")]
    public async Task<IActionResult> Forgot([FromBody] ForgotDto dto, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(dto.Credential))
            return BadRequest(new { message = "credential requerida" });

        ulong? userId = null;
        string? email = null;
        string? passwordPHC = null;
        int? estaActivo = null;

        await using (var conn = new MySqlConnection(Cs))
        {
            await conn.OpenAsync(ct);
            using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 8 };
            cmd.Parameters.AddWithValue("p_Identificador", dto.Credential!.Trim());
            using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                userId = Convert.ToUInt64(rd["UsuarioId"]);
                email  = rd["Email"] as string;
                passwordPHC = rd["PasswordHash"] as string; // lo expone tu SP
                estaActivo = Convert.ToInt32(rd["EstaActivo"]);
            }
        }

        if (userId is null || string.IsNullOrWhiteSpace(email) || estaActivo == 0)
            return Ok(new { ok = true, message = "Si existe, recibirá un correo.", elapsedMs = sw.ElapsedMilliseconds }); // anti-enumeración

        // Token stateless (30 min) con huella del password actual
        var token = ResetToken.Create(userId!.Value, passwordPHC ?? "", TimeSpan.FromMinutes(30), ResetSecret);
        var link  = $"{ResetUrlBase}?token={token}";
        var body = $@"<p>Has solicitado restablecer tu contraseña.</p>
                      <p><a href=""{link}"">Restablecer contraseña</a></p>
                      <p>El enlace vence en 30 minutos.</p>";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20)); // SLA reset < 30 s
        await _email.SendAsync(email!, "Reinicio de contraseña", body, ct: cts.Token);

        return Ok(new { ok = true, message = "Correo enviado si el usuario existe.", elapsedMs = sw.ElapsedMilliseconds });
    }

    // POST /api/password/reset
    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] ResetDto dto, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (string.IsNullOrWhiteSpace(dto.Token) || string.IsNullOrWhiteSpace(dto.NewPassword))
            return BadRequest(new { message = "token y newPassword requeridos" });

        var (ok, userId, fpFromToken, _, err) = ResetToken.Validate(dto.Token!, ResetSecret);
        if (!ok) return Ok(new { ok = false, message = $"Token inválido: {err}" });

        string? currentPHC = null;
        await using (var conn = new MySqlConnection(Cs))
        {
            await conn.OpenAsync(ct);
            using var cmd = new MySqlCommand("sp_usuarios_obtener_por_id", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 8 };
            cmd.Parameters.AddWithValue("p_UsuarioId", userId);
            using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) currentPHC = rd["PasswordHash"] as string;
        }

        if (string.IsNullOrEmpty(currentPHC)) return Ok(new { ok = false, message = "Usuario no encontrado" });

        // Comparar huella para invalidar tokens viejos tras un cambio de password
        var fpNow = ResetToken.Fingerprint(currentPHC!);
        if (!TimeSafeEquals(fpFromToken, fpNow)) return Ok(new { ok = false, message = "Token expirado o ya utilizado" });

        // Hash nuevo (BCrypt/PHC)
        var newPHC = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);

        int codigo; string mensaje;
        await using (var conn = new MySqlConnection(Cs))
        {
            await conn.OpenAsync(ct);
            using var cmd = new MySqlCommand("sp_usuarios_cambiar_password", conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 8 };
            cmd.Parameters.AddWithValue("p_UsuarioId", userId);
            cmd.Parameters.AddWithValue("p_PasswordPHCNew", newPHC);
            var pCodigo = new MySqlParameter("p_Codigo", MySqlDbType.Int32) { Direction = ParameterDirection.Output };
            var pMensaje= new MySqlParameter("p_Mensaje", MySqlDbType.VarChar, 255) { Direction = ParameterDirection.Output };
            cmd.Parameters.Add(pCodigo); cmd.Parameters.Add(pMensaje);

            await cmd.ExecuteNonQueryAsync(ct);
            codigo = (int)(pCodigo.Value ?? 0);
            mensaje = (string)(pMensaje.Value ?? "OK");
        }

        if (codigo != 0) return Ok(new { ok = false, message = mensaje });

        sw.Stop();
        return Ok(new { ok = true, message = "Contraseña actualizada", elapsedMs = sw.ElapsedMilliseconds });
    }

    static bool TimeSafeEquals(string a, string b)
    {
        if (a is null || b is null || a.Length != b.Length) return false;
        int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
