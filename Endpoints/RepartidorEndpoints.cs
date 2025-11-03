// BaseUsuarios.Api/Endpoints/RepartidorEndpoints.cs
using System;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class RepartidorEndpoints
    {
        public static void MapRepartidorEndpoints(this WebApplication app)
        {
            // GET /api/repartidor/orders-ready?limit=1000
            // GET /api/repartidor/orders-ready?limit=1000&repartidorUsuarioId=123 (opcional: sólo las asignadas a ese repartidor)
            app.MapGet("/api/repartidor/orders-ready", async (HttpContext ctx, IConfiguration cfg) =>
            {
                var q = ctx.Request.Query;

                int limit = 1000;
                if (q.TryGetValue("limit", out var qL) && int.TryParse(qL, out var lim))
                    limit = Math.Clamp(lim, 1, 5000);

                long? repartidorUsuarioId = null;
                if (q.TryGetValue("repartidorUsuarioId", out var qR) && long.TryParse(qR, out var rid))
                    repartidorUsuarioId = rid;

                string csCompras =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                using var cn = new MySqlConnection(csCompras);

                const string sqlAllReady = @"
SELECT
  o.id,
  o.folio,
  o.usuario_id,
  o.total          AS price,
  o.metodo_pago,
  o.estado_pago,
  o.creado_en,
  d.descripcion    AS address,
  d.telefono       AS phone
FROM ordenes o
JOIN estados es         ON es.id = o.estado_actual_id
LEFT JOIN direcciones d ON d.id = o.direccion_id
WHERE es.codigo = 'READY'
ORDER BY o.creado_en DESC
LIMIT @limit;";

                const string sqlReadyForCourier = @"
SELECT
  o.id,
  o.folio,
  o.usuario_id,
  o.total          AS price,
  o.metodo_pago,
  o.estado_pago,
  o.creado_en,
  d.descripcion    AS address,
  d.telefono       AS phone
FROM ordenes o
JOIN estados es         ON es.id = o.estado_actual_id
JOIN entregas en        ON en.orden_id = o.id
LEFT JOIN direcciones d ON d.id = o.direccion_id
WHERE es.codigo = 'READY'
  AND en.repartidor_usuario_id = @repartidorUsuarioId
ORDER BY o.creado_en DESC
LIMIT @limit;";

                var rows = await cn.QueryAsync(
                    repartidorUsuarioId.HasValue ? sqlReadyForCourier : sqlAllReady,
                    new { repartidorUsuarioId, limit }
                );

                return Results.Ok(new { items = rows });
            });
        }
    }
}
