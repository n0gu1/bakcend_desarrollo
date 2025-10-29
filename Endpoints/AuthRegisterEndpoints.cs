// Endpoints/AuthRegisterEndpoints.cs
using System.Data;
using System.Text.RegularExpressions;
using BaseUsuarios.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class AuthRegisterEndpoints
    {
        public static IEndpointRouteBuilder MapAuthRegisterEndpoints(this IEndpointRouteBuilder app)
        {
            var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();
            string GetCs() => cfg.GetConnectionString("Default")!;

            app.MapPost("/api/auth/register", async (RegisterDto dto, ILoggerFactory lf) =>
            {
                var log = lf.CreateLogger("auth.register");

                // Validaciones mínimas
                if (string.IsNullOrWhiteSpace(dto.Email) ||
                    string.IsNullOrWhiteSpace(dto.Nickname) ||
                    string.IsNullOrWhiteSpace(dto.Password))
                {
                    return Results.Problem("Email, Nickname y Password son obligatorios.", statusCode: 400);
                }

                // Fecha de nacimiento (Date o texto)
                DateTime birth;
                if (dto.Birthdate.HasValue) birth = dto.Birthdate.Value.Date;
                else if (!string.IsNullOrWhiteSpace(dto.BirthdateText) && DateTime.TryParse(dto.BirthdateText, out var b2)) birth = b2.Date;
                else return Results.Problem("Fecha de nacimiento inválida.", statusCode: 422);

                // Acepta dataURL o base64 “puro”
                static byte[]? TryParseDataUrl(string? s, out string? mime)
                {
                    mime = null;
                    if (string.IsNullOrWhiteSpace(s)) return null;
                    var m = Regex.Match(s, @"^data:(?<mime>[^;]+);base64,(?<data>.+)$");
                    string base64 = m.Success ? m.Groups["data"].Value : s!;
                    if (m.Success) mime = m.Groups["mime"].Value;
                    try { return Convert.FromBase64String(base64); } catch { return null; }
                }

                var foto1 = TryParseDataUrl(dto.PhotoBase64, out var mime1);
                var foto2 = TryParseDataUrl(dto.Photo2Base64, out var mime2);
                if (foto1 != null && string.IsNullOrWhiteSpace(dto.PhotoMime)) dto.PhotoMime = mime1;
                if (foto2 != null && string.IsNullOrWhiteSpace(dto.Photo2Mime)) dto.Photo2Mime = mime2;

                // Hash de password
                var phc = BCrypt.Net.BCrypt.HashPassword(dto.Password);

                try
                {
                    await using var conn = new MySqlConnection(GetCs());
                    await conn.OpenAsync();

                    await using var cmd = new MySqlCommand("sp_usuarios_crear", conn)
                    { CommandType = CommandType.StoredProcedure };

                    cmd.Parameters.AddWithValue("p_Email", dto.Email.Trim());
                    cmd.Parameters.AddWithValue("p_Telefono", (object?)(string.IsNullOrWhiteSpace(dto.Phone) ? null : dto.Phone.Trim()) ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("p_FechaNacimiento", birth);
                    cmd.Parameters.AddWithValue("p_Nickname", dto.Nickname.Trim());
                    cmd.Parameters.AddWithValue("p_PasswordPHC", phc);

                    cmd.Parameters.Add("p_Fotografia",     MySqlDbType.MediumBlob).Value = (object?)foto1 ?? DBNull.Value;
                    cmd.Parameters.Add("p_FotografiaMime", MySqlDbType.VarChar).Value    = (object?)dto.PhotoMime ?? DBNull.Value;
                    cmd.Parameters.Add("p_Fotografia2",    MySqlDbType.MediumBlob).Value = (object?)foto2 ?? DBNull.Value;
                    cmd.Parameters.Add("p_Fotografia2Mime",MySqlDbType.VarChar).Value    = (object?)dto.Photo2Mime ?? DBNull.Value;

                    cmd.Parameters.AddWithValue("p_RolId", dto.RoleId);

                    var pUsuarioId = new MySqlParameter("p_UsuarioId", MySqlDbType.UInt64) { Direction = ParameterDirection.Output };
                    var pCodigo    = new MySqlParameter("p_Codigo",    MySqlDbType.Int32)  { Direction = ParameterDirection.Output };
                    var pMensaje   = new MySqlParameter("p_Mensaje",   MySqlDbType.VarChar, 255) { Direction = ParameterDirection.Output };
                    cmd.Parameters.Add(pUsuarioId);
                    cmd.Parameters.Add(pCodigo);
                    cmd.Parameters.Add(pMensaje);

                    await cmd.ExecuteNonQueryAsync();

                    var code = (int)(pCodigo.Value ?? 0);
                    var msg  = (string?)(pMensaje.Value?.ToString() ?? "OK");

                    log.LogInformation("sp_usuarios_crear code={Code}, msg={Msg}", code, msg);

                    if (code != 0)
                        return Results.Ok(new { success = false, code, message = msg });

                    var newId = (ulong)(pUsuarioId.Value ?? 0);
                    return Results.Ok(new { success = true, id = newId, message = msg });
                }
                catch (Exception ex)
                {
                    var errId = Guid.NewGuid().ToString("N")[..8];
                    log.LogError(ex, "Register failed ({ErrId})", errId);
                    return Results.Problem("Error al registrar usuario. ERR-" + errId, statusCode: 500);
                }
            })
            .WithName("AuthRegister")
            .WithTags("Auth");

            return app;
        }
    }
}
