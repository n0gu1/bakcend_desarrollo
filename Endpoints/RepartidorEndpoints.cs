// BaseUsuarios.Api/Endpoints/RepartidorEndpoints.cs
using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class RepartidorEndpoints
    {
        // ===== DTOs (body) =====
        public record ConfirmReceivedReq(long? repartidorUsuarioId, decimal? lat, decimal? lng);
        public record ConfirmPaymentReq(string method, string? reference, long? repartidorUsuarioId);
        public record FinishDeliveryReq(long? repartidorUsuarioId, decimal? lat, decimal? lng);

        // ===== Modelos internos =====
        private record ReadyOrderRow(
            long id,
            string? folio,
            long usuario_id,
            decimal price,
            string? metodo_pago,
            string? estado_pago,
            DateTime? creado_en,
            string? contacto_nombre,
            string? address,
            string? phone
        );

        private record UserNick(long UsuarioId, string? Nickname);

        public static void MapRepartidorEndpoints(this WebApplication app)
        {
            // ----------------------------------------------------------------
            // Health/ping para probar el proxy de Netlify
            // ----------------------------------------------------------------
            app.MapGet("/api/repartidor/ping", () => Results.Ok(new { ok = true, at = DateTime.UtcNow }));

            // ----------------------------------------------------------------
            // GET /api/repartidor/orders-ready?limit=1000[&repartidorUsuarioId=123]
            //  - Lee READY desde "compras"
            //  - Enriquecer customerName con contacto_nombre o Nickname (otra BD)
            // ----------------------------------------------------------------
            app.MapGet("/api/repartidor/orders-ready", async (HttpContext ctx, IConfiguration cfg) =>
            {
                var q = ctx.Request.Query;

                int limit = 200;
                if (q.TryGetValue("limit", out var qL) && int.TryParse(qL, out var lim))
                    limit = Math.Clamp(lim, 1, 5000);

                long? repartidorUsuarioId = null;
                if (q.TryGetValue("repartidorUsuarioId", out var qR) && long.TryParse(qR, out var rid))
                    repartidorUsuarioId = rid;

                // CS COMPRAS (órdenes)
                string csCompras =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                // CS USUARIOS (Nicknames). Prioriza "Usuarios", luego "Default", luego "UsuariosLocal"
                string? csUsuarios =
                    cfg.GetConnectionString("Usuarios")
                    ?? cfg.GetConnectionString("Default")
                    ?? cfg.GetConnectionString("UsuariosLocal");

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
  d.contacto_nombre,
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
  d.contacto_nombre,
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

                var baseRows = (await cn.QueryAsync<ReadyOrderRow>(
                    repartidorUsuarioId.HasValue ? sqlReadyForCourier : sqlAllReady,
                    new { repartidorUsuarioId, limit }
                )).ToList();

                if (baseRows.Count == 0)
                    return Results.Ok(new { items = Array.Empty<object>() });

                // Nicknames en otra BD (si hay cadena)
                var nickByUser = new Dictionary<long, string?>(capacity: baseRows.Count);
                if (!string.IsNullOrWhiteSpace(csUsuarios))
                {
                    try
                    {
                        using var cnU = new MySqlConnection(csUsuarios);
                        var ids = baseRows.Select(r => r.usuario_id).Distinct().ToArray();
                        if (ids.Length > 0)
                        {
                            var rowsUsers = await cnU.QueryAsync<UserNick>(
                                "SELECT UsuarioId, Nickname FROM usuarios WHERE UsuarioId IN @ids", new { ids });
                            foreach (var u in rowsUsers)
                                nickByUser[u.UsuarioId] = u.Nickname;
                        }
                    }
                    catch
                    {
                        // Si la BD usuarios falla, seguimos con lo disponible.
                    }
                }

                var items = baseRows.Select(r =>
                {
                    var customerName =
                        !string.IsNullOrWhiteSpace(r.contacto_nombre) ? r.contacto_nombre!.Trim()
                        : (nickByUser.TryGetValue(r.usuario_id, out var nk) && !string.IsNullOrWhiteSpace(nk)) ? nk!.Trim()
                        : $"Cliente Usuario {r.usuario_id}";

                    return new
                    {
                        id = r.id,
                        folio = r.folio ?? r.id.ToString(),
                        usuario_id = r.usuario_id,
                        price = r.price,
                        metodo_pago = r.metodo_pago,
                        estado_pago = r.estado_pago,
                        creado_en = r.creado_en,
                        customerName,
                        address = r.address ?? "",
                        phone = r.phone ?? ""
                    };
                });

                return Results.Ok(new { items });
            });

            // ----------------------------------------------------------------
            // POST /api/repartidor/orders/{folio}/confirm-received
            //  - Asegura fila en ENTREGAS
            //  - Pone estado = 'en_ruta'
            //  - Solo permitido si la orden está en READY
            // ----------------------------------------------------------------
            app.MapPost("/api/repartidor/orders/{folio}/confirm-received",
                async (string folio, ConfirmReceivedReq body, IConfiguration cfg) =>
            {
                string cs =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                using var cn = new MySqlConnection(cs);
                await cn.OpenAsync();
                using var tx = await cn.BeginTransactionAsync();

                var ord = await cn.QuerySingleOrDefaultAsync<(long Id, long EstadoId)>(
                    @"SELECT id AS Id, estado_actual_id AS EstadoId
                      FROM ordenes WHERE folio=@folio",
                    new { folio }, tx);

                if (ord.Id == 0)
                    return Results.NotFound(new { error = "Orden no encontrada." });

                var estadoReadyId = await cn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM estados WHERE proceso_id=1 AND codigo='READY' LIMIT 1",
                    transaction: tx);

                if (estadoReadyId == null || ord.EstadoId != estadoReadyId.Value)
                    return Results.Conflict(new { error = "La orden no está en estado READY." });

                var entregaId = await cn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM entregas WHERE orden_id=@oid",
                    new { oid = ord.Id }, tx);

                if (entregaId == null)
                {
                    await cn.ExecuteAsync(
                        @"INSERT INTO entregas (orden_id, repartidor_usuario_id, estado)
                          VALUES (@oid, @rid, 'en_ruta')",
                        new { oid = ord.Id, rid = body.repartidorUsuarioId }, tx);
                }
                else
                {
                    await cn.ExecuteAsync(
                        @"UPDATE entregas
                          SET estado='en_ruta',
                              repartidor_usuario_id = COALESCE(@rid, repartidor_usuario_id)
                          WHERE id=@eid",
                        new { eid = entregaId, rid = body.repartidorUsuarioId }, tx);
                }

                await tx.CommitAsync();
                return Results.Ok(new { ok = true, folio, status = "en_ruta" });
            });

            // ----------------------------------------------------------------
            // POST /api/repartidor/orders/{folio}/confirm-payment
            //  - method: 'cash' | 'transfer' | 'card'
            //  - cash => pagos.estado=pagado (+pagado_en); orden.estado_pago='pagado'
            //  - transfer/card => pagos.estado=autorizado; orden.estado_pago='autorizado'
            // ----------------------------------------------------------------
            app.MapPost("/api/repartidor/orders/{folio}/confirm-payment",
                async (string folio, ConfirmPaymentReq body, IConfiguration cfg) =>
            {
                string cs =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                var method = (body.method ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(method) || !(method is "cash" or "transfer" or "card"))
                    return Results.BadRequest(new { error = "method debe ser cash|transfer|card." });

                using var cn = new MySqlConnection(cs);
                await cn.OpenAsync();
                using var tx = await cn.BeginTransactionAsync();

                var ord = await cn.QuerySingleOrDefaultAsync(
                    @"SELECT id, total FROM ordenes WHERE folio=@folio",
                    new { folio }, tx);
                if (ord == null)
                    return Results.NotFound(new { error = "Orden no encontrada." });

                var ahora = DateTime.UtcNow;
                DateTime? pagadoEn = method == "cash" ? ahora : (DateTime?)null; // <— evita error DateTime vs null

                string metodoPago = method == "cash" ? "efectivo" : "tarjeta";
                string estadoPago = method == "cash" ? "pagado" : "autorizado";

                await cn.ExecuteAsync(
                    @"INSERT INTO pagos (orden_id, metodo, proveedor, referencia_proveedor, estado, monto, pagado_en, datos)
                      VALUES (@oid, @metodo, 'ninguno', @refe, @estado, @monto, @pagado, JSON_OBJECT('source','repartidor'))",
                    new
                    {
                        oid = (long)ord.id,
                        metodo = metodoPago,
                        refe = body.reference,
                        estado = estadoPago,
                        monto = (decimal)ord.total,
                        pagado = pagadoEn
                    }, tx);

                await cn.ExecuteAsync(
                    @"UPDATE ordenes
                      SET estado_pago=@estado, actualizado_en=@ahora
                      WHERE id=@oid",
                    new { estado = estadoPago, ahora, oid = (long)ord.id }, tx);

                if (method == "cash")
                {
                    await cn.ExecuteAsync(
                        @"UPDATE entregas
                          SET cobrado_efectivo_en = COALESCE(cobrado_efectivo_en, @ahora),
                              repartidor_usuario_id = COALESCE(@rid, repartidor_usuario_id)
                          WHERE orden_id=@oid",
                        new { ahora, oid = (long)ord.id, rid = body.repartidorUsuarioId }, tx);
                }

                await tx.CommitAsync();
                return Results.Ok(new { ok = true, folio, payment = new { method, state = estadoPago } });
            });

            // ----------------------------------------------------------------
            // POST /api/repartidor/orders/{folio}/finish-delivery
            //  - entregas: estado='entregado', entregado_en=now
            //  - ordenes: estado_actual_id => DONE
            //  - historial_flujo (+ transicion SET-3->4)
            // ---------------------------------------------------------------->
            app.MapPost("/api/repartidor/orders/{folio}/finish-delivery",
                async (string folio, FinishDeliveryReq body, IConfiguration cfg) =>
            {
                string cs =
                    cfg.GetConnectionString("Compras")
                    ?? cfg.GetConnectionString("ComprasLocal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Compras/ComprasLocal no configurada.");

                using var cn = new MySqlConnection(cs);
                await cn.OpenAsync();
                using var tx = await cn.BeginTransactionAsync();

                var ord = await cn.QuerySingleOrDefaultAsync(
                    @"SELECT id, estado_actual_id FROM ordenes WHERE folio=@folio",
                    new { folio }, tx);
                if (ord == null)
                    return Results.NotFound(new { error = "Orden no encontrada." });

                var estadoDoneId = await cn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM estados WHERE proceso_id=1 AND codigo='DONE' LIMIT 1",
                    transaction: tx);
                var transSet34Id = await cn.ExecuteScalarAsync<long?>(
                    "SELECT id FROM transiciones WHERE proceso_id=1 AND codigo='SET-3->4' LIMIT 1",
                    transaction: tx);

                if (estadoDoneId == null || transSet34Id == null)
                    return Results.Problem("Faltan estados/transición (DONE o SET-3->4).", statusCode: 500);

                var ahora = DateTime.UtcNow;

                await cn.ExecuteAsync(
                    @"UPDATE entregas
                      SET estado='entregado',
                          entregado_en = COALESCE(entregado_en, @ahora),
                          repartidor_usuario_id = COALESCE(@rid, repartidor_usuario_id)
                      WHERE orden_id=@oid",
                    new { ahora, oid = (long)ord.id, rid = body.repartidorUsuarioId }, tx);

                await cn.ExecuteAsync(
                    @"UPDATE ordenes
                      SET estado_actual_id=@done, actualizado_en=@ahora
                      WHERE id=@oid",
                    new { done = estadoDoneId.Value, ahora, oid = (long)ord.id }, tx);

                await cn.ExecuteAsync(
                    @"INSERT INTO historial_flujo (objeto_tipo, objeto_id, transicion_id, estado_id, usuario_id, notas, creado_en)
                      VALUES ('orden', @oid, @tr, @st, @uid, 'Entrega finalizada por repartidor', @ahora)",
                    new
                    {
                        oid = (long)ord.id,
                        tr = transSet34Id.Value,
                        st = estadoDoneId.Value,
                        uid = body.repartidorUsuarioId,
                        ahora
                    }, tx);

                await tx.CommitAsync();
                return Results.Ok(new { ok = true, folio, state = "DONE" });
            });
        }
    }
}