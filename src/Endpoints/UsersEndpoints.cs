// BaseUsuarios.Api/Endpoints/UsersEndpoints.cs
using System.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class UsersEndpoints
    {
        public static IEndpointRouteBuilder MapUsersEndpoints(this IEndpointRouteBuilder app)
        {
            // Mapeamos en ambos prefijos para evitar 404 según despliegue:
            // - /api/default/users/...
            // - /api/users/...
            var gDefault = app.MapGroup("/api/default/users").WithTags("Users");
            var gApi     = app.MapGroup("/api/users").WithTags("Users");

            void MapRoutes(RouteGroupBuilder g)
            {
                // GET /.../users/{userId}/role  -> { userId, rolId }
                g.MapGet("/{userId:long}/role", async (long userId, IConfiguration cfg) =>
                {
                    var cs = cfg.GetConnectionString("Default")
                          ?? cfg.GetConnectionString("UsuariosDb")
                          ?? cfg["ConnectionStrings:Default"];

                    if (string.IsNullOrWhiteSpace(cs))
                        return Results.Problem("ConnectionStrings:Default no configurada.", statusCode: 500);

                    int? rolId = null;

                    await using (var conn = new MySqlConnection(cs))
                    {
                        await conn.OpenAsync();
                        await using var cmd = new MySqlCommand(
                            "SELECT RolId FROM usuarios WHERE UsuarioId=@u LIMIT 1;", conn);
                        cmd.Parameters.AddWithValue("@u", userId);

                        var o = await cmd.ExecuteScalarAsync();
                        if (o != null && o != DBNull.Value)
                            rolId = Convert.ToInt32(o);
                    }

                    return rolId is null
                        ? Results.NotFound(new { message = "Usuario no encontrado" })
                        : Results.Ok(new { userId, rolId });
                })
                .WithName("UserRole");

                // (Opcional) GET /.../users/{userId}/summary -> { userId, nickname, email, rolId }
                g.MapGet("/{userId:long}/summary", async (long userId, IConfiguration cfg) =>
                {
                    var cs = cfg.GetConnectionString("Default")
                          ?? cfg.GetConnectionString("UsuariosDb")
                          ?? cfg["ConnectionStrings:Default"];

                    if (string.IsNullOrWhiteSpace(cs))
                        return Results.Problem("ConnectionStrings:Default no configurada.", statusCode: 500);

                    await using var conn = new MySqlConnection(cs);
                    await conn.OpenAsync();

                    await using var cmd = new MySqlCommand(@"
                        SELECT UsuarioId, Nickname, Email, RolId
                        FROM usuarios
                        WHERE UsuarioId = @u
                        LIMIT 1;", conn);
                    cmd.Parameters.AddWithValue("@u", userId);

                    await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
                    if (!await rd.ReadAsync())
                        return Results.NotFound(new { message = "Usuario no encontrado" });

                    var uid   = rd.GetInt64(rd.GetOrdinal("UsuarioId"));
                    var nick  = rd.IsDBNull(rd.GetOrdinal("Nickname")) ? null : rd.GetString(rd.GetOrdinal("Nickname"));
                    var email = rd.IsDBNull(rd.GetOrdinal("Email"))    ? null : rd.GetString(rd.GetOrdinal("Email"));
                    var rolId = rd.IsDBNull(rd.GetOrdinal("RolId"))    ? (int?)null : rd.GetInt32(rd.GetOrdinal("RolId"));

                    return Results.Ok(new { userId = uid, nickname = nick, email, rolId });
                })
                .WithName("UserSummary");
            }

            MapRoutes(gDefault);
            MapRoutes(gApi);

            return app;
        }
    }
}
