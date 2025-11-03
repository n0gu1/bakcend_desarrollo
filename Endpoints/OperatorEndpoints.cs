// BaseUsuarios.Api/Endpoints/OperatorEndpoints.cs
using System;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class OperatorEndpoints
    {
        public static void MapOperatorEndpoints(this WebApplication app)
        {
            // GET /api/operator/orders-assigned?estado=CRE|PROC|READY&limit=50&operadorUsuarioId=123
            app.MapGet("/api/operator/orders-assigned", async (HttpContext ctx, IConfiguration cfg, ILoggerFactory lf) =>
            {
                var log = lf.CreateLogger("orders-assigned");
                try
                {
                    var q = ctx.Request.Query;

                    string? estado = q.TryGetValue("estado", out var qE) ? qE.ToString() : null;
                    string? estadoUpper = string.IsNullOrWhiteSpace(estado) ? null : estado.ToUpperInvariant();

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

                    await using var cn = new MySqlConnection(csCompras);
                    await cn.OpenAsync();

                    const string sql = @"
SELECT  o.id,
        -- folio completo con fallback por si viene vacío/nulo
        COALESCE(NULLIF(o.folio,''), CONCAT(DATE_FORMAT(o.creado_en,'%Y%m%d'),'-',LPAD(o.id,4,'0'))) AS folio,
        o.usuario_id,
        o.creado_en
FROM ordenes o
JOIN estados e            ON e.id = o.estado_actual_id
JOIN procesos pr          ON pr.id = o.proceso_id AND pr.codigo = 'ORD'
JOIN orden_operadores oo  ON oo.orden_id = o.id   -- <== SOLO asignadas
WHERE (@estado IS NULL OR e.codigo = @estado)
  AND (@operadorUsuarioId IS NULL OR oo.operador_usuario_id = @operadorUsuarioId)
ORDER BY o.creado_en DESC
LIMIT @limit;";

                    var rows = await cn.QueryAsync(sql, new { estado = estadoUpper, operadorUsuarioId, limit });
                    return Results.Ok(new { items = rows });
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Fallo /api/operator/orders-assigned");
                    return Results.Problem(
                        statusCode: 500,
                        title: "Error al obtener órdenes asignadas",
                        detail: ex.Message
                    );
                }
            });

            // ===== NUEVO =====
            // GET /api/courier/orders-ready?limit=200
            // Trae TODAS las órdenes READY (sin filtrar por operador).
            app.MapGet("/api/courier/orders-ready", async (HttpContext ctx, IConfiguration cfg, ILoggerFactory lf) =>
            {
                var log = lf.CreateLogger("orders-ready");
                try
                {
                    var q = ctx.Request.Query;
                    int limit = 200;
                    if (q.TryGetValue("limit", out var qL) && int.TryParse(qL, out var lim))
                        limit = Math.Clamp(lim, 1, 1000);

                    string csCompras =
                        cfg.GetConnectionString("Compras")
                        ?? cfg.GetConnectionString("ComprasLocal")
                        ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                    await using var cn = new MySqlConnection(csCompras);
                    await cn.OpenAsync();

                    const string sqlReady = @"
SELECT
  o.id,
  COALESCE(NULLIF(o.folio,''), CONCAT(DATE_FORMAT(o.creado_en,'%Y%m%d'),'-',LPAD(o.id,4,'0'))) AS folio,
  COALESCE(u.nickname, u.nombre, u.name, 'Cliente') AS customerName,
  COALESCE(o.nfc_tipo,'Link') AS nfcType,
  COALESCE(o.total,0) AS price,
  o.creado_en
FROM ordenes o
JOIN procesos pr ON pr.id = o.proceso_id AND pr.codigo = 'ORD'
JOIN estados  es ON es.id = o.estado_actual_id AND es.codigo = 'READY'
LEFT JOIN usuarios u ON u.id = o.usuario_id
ORDER BY o.creado_en DESC
LIMIT @limit;";

                    var items = await cn.QueryAsync(sqlReady, new { limit });
                    return Results.Ok(new { items });
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Fallo /api/courier/orders-ready");
                    return Results.Problem(
                        statusCode: 500,
                        title: "Error al obtener órdenes READY",
                        detail: ex.Message
                    );
                }
            });
        }
    }
}
