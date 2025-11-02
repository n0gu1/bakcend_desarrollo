// BaseUsuarios.Api/Endpoints/OperatorEndpoints.cs
using System;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class OperatorEndpoints
    {
        public static void MapOperatorEndpoints(this WebApplication app)
        {
            // GET /api/operator/orders-assigned?estado=CRE&limit=50&operadorUsuarioId=123
            app.MapGet("/api/operator/orders-assigned", async (HttpContext ctx, IConfiguration cfg) =>
            {
                var q = ctx.Request.Query;

                string? estado = q.TryGetValue("estado", out var qE) ? qE.ToString() : null;
                int limit = 100;
                if (q.TryGetValue("limit", out var qL) && int.TryParse(qL, out var lim))
                    limit = Math.Clamp(lim, 1, 500);

                long? operadorUsuarioId = null;
                if (q.TryGetValue("operadorUsuarioId", out var qOp) && long.TryParse(qOp, out var vid))
                    operadorUsuarioId = vid;

                string csCompras =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                using var cn = new MySqlConnection(csCompras);

                const string sql = @"
SELECT  o.id,
        o.folio,
        o.usuario_id,
        o.creado_en
FROM ordenes o
JOIN estados e           ON e.id = o.estado_actual_id
JOIN orden_operadores oo ON oo.orden_id = o.id
WHERE (@estado IS NULL OR e.codigo = @estado)
  AND (@operadorUsuarioId IS NULL OR oo.operador_usuario_id = @operadorUsuarioId)
ORDER BY o.creado_en DESC
LIMIT @limit;";

                var rows = await cn.QueryAsync(sql, new { estado, operadorUsuarioId, limit });
                return Results.Ok(new { items = rows });
            });
        }
    }
}
