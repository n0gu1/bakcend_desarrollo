// Endpoints/AuthLoginEndpoints.cs
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
    public static class AuthLoginEndpoints
    {
        public static IEndpointRouteBuilder MapAuthLoginEndpoints(this IEndpointRouteBuilder app)
        {
            var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();
            string GetCs() => cfg.GetConnectionString("Default")!;

            // /api/auth/exists?credential=...
            app.MapGet("/api/auth/exists", async (string credential) =>
            {
                if (string.IsNullOrWhiteSpace(credential))
                    return Results.BadRequest(new { message = "credential es requerido" });

                await using var conn = new MySqlConnection(GetCs());
                await conn.OpenAsync();

                // Debe existir este SP que devuelva UsuarioId y EstaActivo
                await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn)
                { CommandType = CommandType.StoredProcedure };
                cmd.Parameters.AddWithValue("p_Identificador", credential.Trim());

                await using var rd = await cmd.ExecuteReaderAsync();
                if (!rd.Read()) return Results.Ok(new { exists = false });

                var id     = Convert.ToUInt64(rd["UsuarioId"]);
                var active = Convert.ToInt32(rd["EstaActivo"]) == 1;
                return Results.Ok(new { exists = true, id, active });
            })
            .WithName("AuthExists")
            .WithTags("Auth");

            // /api/auth/login
            app.MapPost("/api/auth/login", async (LoginDto dto, ILoggerFactory lf) =>
            {
                var log = lf.CreateLogger("auth.login");

                if (string.IsNullOrWhiteSpace(dto.Credential) || string.IsNullOrWhiteSpace(dto.Password))
                    return Results.Ok(new { success = false, message = "Credenciales requeridas" });

                try
                {
                    await using var conn = new MySqlConnection(GetCs());
                    await conn.OpenAsync();

                    await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn)
                    { CommandType = CommandType.StoredProcedure };
                    cmd.Parameters.AddWithValue("p_Identificador", dto.Credential.Trim());

                    await using var rd = await cmd.ExecuteReaderAsync();
                    if (!rd.Read())
                        return Results.Ok(new { success = false, message = "Usuario no encontrado" });

                    var id     = Convert.ToUInt64(rd["UsuarioId"]);
                    var phc    = rd["PasswordHash"] as string ?? "";
                    var activo = Convert.ToInt32(rd["EstaActivo"]) == 1;
                    if (!activo) return Results.Ok(new { success = false, message = "Usuario inactivo" });

                    bool ok = !string.IsNullOrWhiteSpace(phc) && phc.StartsWith("$2") && BCrypt.Net.BCrypt.Verify(dto.Password, phc);

                    return ok
                        ? Results.Ok(new { success = true, message = "OK", id })
                        : Results.Ok(new { success = false, message = "Credenciales inválidas" });
                }
                catch (Exception ex)
                {
                    var errId = Guid.NewGuid().ToString("N")[..8];
                    log.LogError(ex, "Login failed ({ErrId})", errId);
                    return Results.Problem("Error al iniciar sesión. ERR-" + errId, statusCode: 500);
                }
            })
            .WithName("AuthLogin")
            .WithTags("Auth");

            // (Opcional) /api/auth/login-camera
            app.MapPost("/api/auth/login-camera", async (LoginCameraDto dto, ILoggerFactory lf) =>
            {
                var log = lf.CreateLogger("auth.loginCam");

                if (string.IsNullOrWhiteSpace(dto.Credential) || string.IsNullOrWhiteSpace(dto.Password))
                    return Results.Ok(new { success = false, message = "Credenciales requeridas" });

                // Si quieres hacer match biométrico real, integra aquí tu lógica con la foto.
                // Por ahora verificamos credenciales igual que /login.

                try
                {
                    await using var conn = new MySqlConnection(GetCs());
                    await conn.OpenAsync();

                    await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn)
                    { CommandType = CommandType.StoredProcedure };
                    cmd.Parameters.AddWithValue("p_Identificador", dto.Credential.Trim());

                    await using var rd = await cmd.ExecuteReaderAsync();
                    if (!rd.Read())
                        return Results.Ok(new { success = false, message = "Usuario no encontrado" });

                    var id     = Convert.ToUInt64(rd["UsuarioId"]);
                    var phc    = rd["PasswordHash"] as string ?? "";
                    var activo = Convert.ToInt32(rd["EstaActivo"]) == 1;
                    if (!activo) return Results.Ok(new { success = false, message = "Usuario inactivo" });

                    bool ok = !string.IsNullOrWhiteSpace(phc) && phc.StartsWith("$2") && BCrypt.Net.BCrypt.Verify(dto.Password, phc);

                    // Si necesitas validar que envió foto:
                    // if (ok && string.IsNullOrWhiteSpace(dto.PhotoBase64)) return Results.Ok(new { success=false, message="Foto requerida" });

                    return ok
                        ? Results.Ok(new { success = true, message = "OK", id })
                        : Results.Ok(new { success = false, message = "Credenciales inválidas" });
                }
                catch (Exception ex)
                {
                    var errId = Guid.NewGuid().ToString("N")[..8];
                    log.LogError(ex, "LoginCam failed ({ErrId})", errId);
                    return Results.Problem("Error al iniciar sesión (cámara). ERR-" + errId, statusCode: 500);
                }
            })
            .WithName("AuthLoginCamera")
            .WithTags("Auth");

            return app;
        }
    }
}
