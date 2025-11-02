// BaseUsuarios.Api/Endpoints/SupervisorEndpoints.cs
using System;
using System.Linq;
using System.Collections.Generic;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Caching.Memory;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class SupervisorEndpoints
    {
        public record OperatorDto(long UsuarioId, string Nickname, string? Email);
        public record AssignOperatorReq(long? OperadorUsuarioId);

        public static void MapSupervisorEndpoints(this WebApplication app)
        {
            app.MapGet("/api/supervisor/operators", async (HttpContext ctx, IConfiguration cfg, IMemoryCache cache) =>
            {
                int rolId = 5;
                if (ctx.Request.Query.TryGetValue("rolId", out var qRol) && int.TryParse(qRol, out var rr)) rolId = rr;

                bool soloActivos = ctx.Request.Query.TryGetValue("soloActivos", out var qAct)
                                   && int.TryParse(qAct, out var sa)
                                   && sa == 1;

                int limit = 500;
                if (ctx.Request.Query.TryGetValue("limit", out var qLim) &&
                    int.TryParse(qLim, out var lim) && lim > 0 && lim <= 5000) limit = lim;

                string cacheKey = $"operators:rol={rolId}:act={(soloActivos ? 1 : 0)}:lim={limit}";
                if (cache.TryGetValue(cacheKey, out List<OperatorDto> cached) && cached is not null)
                    return Results.Ok(cached);

                string csUsuarios =
                    cfg.GetConnectionString("UsuariosDb")
                    ?? cfg.GetConnectionString("Default")
                    ?? cfg["ConnectionStrings:UsuariosDb"]
                    ?? throw new InvalidOperationException("ConnectionStrings:UsuariosDb/Default no configurada.");

                const string sql = @"
SELECT u.UsuarioId, u.Nickname, u.Email
FROM   usuarios u
WHERE  u.RolId = @RolId
  AND  (@SoloActivos = 0 OR u.EstaActivo = 1)
ORDER BY u.UsuarioId DESC
LIMIT  @Limit;";

                using var cn = new MySqlConnection(csUsuarios);
                var data = (await cn.QueryAsync<OperatorDto>(sql, new
                {
                    RolId = rolId,
                    SoloActivos = soloActivos ? 1 : 0,
                    Limit = limit
                })).ToList();

                cache.Set(cacheKey, data, new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2)
                });

                return Results.Ok(data);
            });

            app.MapPost("/api/supervisor/orders/{ordenId:long}/assign-operator",
                async (long ordenId, AssignOperatorReq body, IConfiguration cfg) =>
                {
                    try
                    {
                        if (ordenId <= 0) return Results.BadRequest("ordenId inválido.");

                        string csCompras =
                            cfg.GetConnectionString("Compras")
                            ?? cfg.GetConnectionString("ComprasLocal")
                            ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                        using var cn = new MySqlConnection(csCompras);

                        if (body?.OperadorUsuarioId is null)
                        {
                            const string del = "DELETE FROM orden_operadores WHERE orden_id = @ordenId;";
                            await cn.ExecuteAsync(del, new { ordenId });
                            return Results.NoContent();
                        }

                        const string upsert = @"
INSERT INTO orden_operadores (orden_id, operador_usuario_id, asignado_en)
VALUES (@ordenId, @opId, NOW())
ON DUPLICATE KEY UPDATE
  operador_usuario_id = VALUES(operador_usuario_id),
  asignado_en = NOW();";

                        await cn.ExecuteAsync(upsert, new { ordenId, opId = body.OperadorUsuarioId });
                        return Results.NoContent();
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[assign-operator] {ex}");
                        return Results.Problem(title: "Error asignando operador", detail: ex.Message, statusCode: 500);
                    }
                });

            app.MapGet("/api/supervisor/orders/assignments", async (HttpContext ctx, IConfiguration cfg) =>
            {
                var idsCsv = ctx.Request.Query["ids"].ToString();
                if (string.IsNullOrWhiteSpace(idsCsv))
                    return Results.Ok(Array.Empty<object>());

                var ids = idsCsv.Split(',')
                                .Select(s => long.TryParse(s, out var v) ? v : 0)
                                .Where(v => v > 0)
                                .ToArray();
                if (ids.Length == 0)
                    return Results.Ok(Array.Empty<object>());

                string csCompras =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                using var cn = new MySqlConnection(csCompras);

                var rows = await cn.QueryAsync(@"
SELECT
  orden_id             AS OrdenId,
  operador_usuario_id  AS OperadorUsuarioId
FROM orden_operadores
WHERE orden_id IN @ids;", new { ids });

                return Results.Ok(rows);
            });
        }
    }
}
