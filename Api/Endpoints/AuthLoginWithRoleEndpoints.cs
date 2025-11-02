// Endpoints/AuthLoginWithRoleEndpoints.cs
using System.Data;
using BaseUsuarios.Api.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class AuthLoginWithRoleEndpoints
    {
        public static IEndpointRouteBuilder MapAuthLoginWithRoleEndpoints(this IEndpointRouteBuilder app)
        {
            var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();
            string GetCs() => cfg.GetConnectionString("Default")!; // misma conexión que usas para usuarios

            // POST /api/auth/login-with-role
            app.MapPost("/api/auth/login-with-role", async (LoginDto dto) =>
            {
                if (string.IsNullOrWhiteSpace(dto.Credential) || string.IsNullOrWhiteSpace(dto.Password))
                    return Results.Ok(new { success = false, message = "Credenciales requeridas" });

                await using var conn = new MySqlConnection(GetCs());
                await conn.OpenAsync();

                // 1) Buscar usuario por credencial (mismo SP que el login actual)
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

                var ok = !string.IsNullOrWhiteSpace(phc) && phc.StartsWith("$2") && BCrypt.Net.BCrypt.Verify(dto.Password, phc);
                if (!ok) return Results.Ok(new { success = false, message = "Credenciales inválidas" });

                // 2) Conexión ya abierta; leer RolId con SELECT directo
                //    (cerramos/consumimos el reader antes de otro comando)
                //    rd.Dispose() es implícito al salir del using, pero liberamos ya:
                await rd.DisposeAsync();

                await using var cmdRole = new MySqlCommand(
                    "SELECT RolId FROM usuarios WHERE UsuarioId = @id LIMIT 1;", conn);
                cmdRole.Parameters.AddWithValue("@id", id);
                var roleIdObj = await cmdRole.ExecuteScalarAsync();
                byte? roleId = roleIdObj is null || roleIdObj == DBNull.Value
                               ? (byte?)null
                               : Convert.ToByte(roleIdObj);

                return Results.Ok(new { success = true, message = "OK", id, roleId });
            })
            .WithName("AuthLoginWithRole")
            .WithTags("Auth");

            return app;
        }
    }
}
