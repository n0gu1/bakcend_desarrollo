// BaseUsuarios.Api/Endpoints/OrdersEndpoints.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints;

public static class OrdersEndpoints
{
    public record CheckoutAddressReq(string? descripcion, string? contactoNombre, string? telefono, long? areaId, long? puntoEntregaId);
    public record CheckoutReq(long usuarioId, string metodoPago, CheckoutAddressReq? direccion, long? draftItemId);
    public record CheckoutResp(string scope, long usuarioId, int itemsProcesados, List<OrdenCreada> ordenes);
    public record OrdenCreada(long ordenId, string folio, decimal total, long? entregaId);
    public record SetStateReq(string? code, string? codigo, string? note, string? notas);
    public record AddEventReq(string? note, string? notas);

    public static void MapOrdersEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        static IDbConnection OpenConn(HttpContext ctx, bool local)
        {
            var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
            var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")!;
            var conn = new MySqlConnection(cs);
            conn.Open();
            return conn;
        }

        g.MapGet("/{scope:regex((?i)^(local|default)$)}/areas", async (HttpContext ctx, string scope) =>
        {
            var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
            using var db = OpenConn(ctx, local);
            var rows = await db.QueryAsync(@"SELECT id, nombre FROM areas_entrega ORDER BY nombre;");
            return Results.Ok(rows);
        });

        g.MapGet("/{scope:regex((?i)^(local|default)$)}/puntos", async (HttpContext ctx, string scope, long areaId) =>
        {
            var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
            using var db = OpenConn(ctx, local);
            var rows = await db.QueryAsync(@"
                SELECT id, nombre, lat, lng
                FROM puntos_entrega
                WHERE area_id=@areaId
                ORDER BY nombre;", new { areaId });
            return Results.Ok(rows);
        });

        g.MapGet("/{scope:regex((?i)^(local|default)$)}/cart/preview", async (HttpContext ctx, string scope, long usuarioId) =>
        {
            var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
            using var db = OpenConn(ctx, local);

            var carrito = await db.QueryFirstOrDefaultAsync(@"
                SELECT id
                FROM carritos
                WHERE usuario_id=@u AND estado='abierto'
                ORDER BY id DESC
                LIMIT 1;", new { u = usuarioId });

            if (carrito is null)
                return Results.Ok(new { items = Array.Empty<object>(), total = 0m });

            var items = await db.QueryAsync(@"
                SELECT ci.id, ci.carrito_id, ci.producto_id, p.nombre, ci.cantidad, ci.precio_unitario,
                       (ci.cantidad * ci.precio_unitario) AS subtotal
                FROM carrito_items ci
                JOIN productos p ON p.id = ci.producto_id
                WHERE ci.carrito_id=@cid
                ORDER BY ci.id ASC;", new { cid = (long)carrito.id });

            var total = items.Sum(r => (decimal)r.subtotal);
            return Results.Ok(new { items, total });
        });

        g.MapPost("/{scope:regex((?i)^(local|default)$)}/checkout", async (HttpContext ctx, string scope, CheckoutReq body) =>
        {
            if (body.usuarioId <= 0) return Results.BadRequest(new { error = "usuarioId inválido" });
            var metodoPago = (body.metodoPago ?? "").ToLowerInvariant();
            if (metodoPago != "efectivo" && metodoPago != "tarjeta")
                return Results.BadRequest(new { error = "metodoPago debe ser 'efectivo' o 'tarjeta'" });

            var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
            using var db = OpenConn(ctx, local);
            using var tx = db.BeginTransaction();

            var carrito = await db.QueryFirstOrDefaultAsync(@"
                SELECT id
                FROM carritos
                WHERE usuario_id=@u AND estado='abierto'
                ORDER BY id DESC
                LIMIT 1;", new { u = body.usuarioId }, tx);

            if (carrito is null)
                return Results.BadRequest(new { error = "No hay carrito abierto para el usuario." });

            long carritoId = (long)carrito.id;

            var items = (await db.QueryAsync(@"
                SELECT ci.id, ci.producto_id, ci.cantidad, ci.precio_unitario
                FROM carrito_items ci
                WHERE ci.carrito_id=@cid
                ORDER BY ci.id ASC;", new { cid = carritoId }, tx)).ToList();

            if (items.Count == 0)
                return Results.BadRequest(new { error = "El carrito está vacío." });

            long? direccionId = null;
            long? areaId = body.direccion?.areaId;
            long? puntoId = body.direccion?.puntoEntregaId;

            if (body.direccion is not null)
            {
                direccionId = await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO direcciones (usuario_id, area_id, punto_entrega_id, descripcion, contacto_nombre, telefono)
                    VALUES (@u, @a, @p, @d, @c, @t);
                    SELECT LAST_INSERT_ID();",
                    new {
                        u = body.usuarioId,
                        a = areaId,
                        p = puntoId,
                        d = body.direccion.descripcion,
                        c = body.direccion.contactoNombre,
                        t = body.direccion.telefono
                    }, tx);
            }

            var proceso = await db.QueryFirstOrDefaultAsync(
                "SELECT id FROM procesos WHERE codigo='ORD' LIMIT 1;", transaction: tx);
            if (proceso is null)
                return Results.BadRequest(new { error = "No existe proceso 'ORD' en tabla procesos." });

            long procesoId = (long)proceso.id;

            var estadoInicial = await db.QueryFirstOrDefaultAsync(
                "SELECT id FROM estados WHERE proceso_id=@p AND tipo='I' LIMIT 1;",
                new { p = procesoId }, tx);
            if (estadoInicial is null)
                return Results.BadRequest(new { error = "No existe estado inicial (tipo='I') para el proceso 'ORD'." });

            long estadoInicialId = (long)estadoInicial.id;

            var ordenes = new List<OrdenCreada>();
            foreach (var it in items)
            {
                long productoId = (long)it.producto_id;
                int  cantidad   = (int) it.cantidad;
                decimal precio  = (decimal)it.precio_unitario;
                decimal total   = cantidad * precio;

                string folio = await NuevoFolioAsync(db, tx);
                string qrTexto = $"ORD-{folio}";
                long? qrArchivoId = null;

                long ordenId = await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO ordenes
                      (usuario_id, folio, total, proceso_id, estado_actual_id, metodo_pago, estado_pago,
                       direccion_id, area_id, qr_texto, qr_archivo_id, creado_en, actualizado_en)
                    VALUES
                      (@u, @folio, @total, @proceso, @estado, @metodo, 'pendiente',
                       @dir, @area, @qr, @qrfile, NOW(), NOW());
                    SELECT LAST_INSERT_ID();",
                    new {
                        u = body.usuarioId,
                        folio,
                        total,
                        proceso = procesoId,
                        estado  = estadoInicialId,
                        metodo  = metodoPago,
                        dir     = direccionId,
                        area    = areaId,
                        qr      = qrTexto,
                        qrfile  = qrArchivoId
                    }, tx);

                long oiId = await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO orden_items (orden_id, producto_id, cantidad, precio_unitario)
                    VALUES (@o, @p, @c, @pu);
                    SELECT LAST_INSERT_ID();",
                    new { o = ordenId, p = productoId, c = cantidad, pu = precio }, tx);

                var (pidA, pidB) = await CopiarPersonalizacionesAsync(db, tx, (long)it.id, oiId, body.draftItemId);

                if (pidA.HasValue) await UpsertOrdenImagenAsync(db, tx, ordenId, pidA.Value, 'A');
                if (pidB.HasValue) await UpsertOrdenImagenAsync(db, tx, ordenId, pidB.Value, 'B');

                long entregaId = await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO entregas (orden_id, estado)
                    VALUES (@o, 'pendiente');
                    SELECT LAST_INSERT_ID();", new { o = ordenId }, tx);

                long trId = await AsegurarTransicionAsync(db, tx, procesoId, estadoInicialId, estadoInicialId);

                await db.ExecuteAsync(@"
                    INSERT INTO historial_flujo (objeto_tipo, objeto_id, transicion_id, estado_id, usuario_id, notas, creado_en)
                    VALUES ('orden', @id, @tr, @st, @u, 'Creación de orden', NOW());",
                    new { id = ordenId, tr = trId, st = estadoInicialId, u = body.usuarioId }, tx);

                ordenes.Add(new OrdenCreada(ordenId, folio, total, entregaId));
            }

            await db.ExecuteAsync("UPDATE carritos SET estado='cerrado', actualizado_en=NOW() WHERE id=@cid;",
                new { cid = carritoId }, tx);

            tx.Commit();
            return Results.Ok(new CheckoutResp(local ? "local" : "default", body.usuarioId, items.Count, ordenes));
        });

        static async Task<IResult> TrackingHandler(HttpContext ctx, bool local, string folio)
        {
            using var db = OpenConn(ctx, local);

            var ord = await db.QueryFirstOrDefaultAsync(@"
                SELECT o.id, o.folio, o.usuario_id, o.total, o.estado_actual_id, o.proceso_id,
                       es.codigo AS estado_codigo, es.nombre AS estado_nombre, es.paso_publico
                FROM ordenes o
                LEFT JOIN estados es ON es.id = o.estado_actual_id
                WHERE o.folio = @folio
                LIMIT 1;", new { folio });

            if (ord is null) return Results.NotFound(new { error = "Orden no encontrada" });

            var entrega = await db.QueryFirstOrDefaultAsync(@"
                SELECT id, orden_id, repartidor_usuario_id, estado, cobrado_efectivo_en, entregado_en
                FROM entregas
                WHERE orden_id = @o
                LIMIT 1;", new { o = (long)ord.id });

            var eventos = await db.QueryAsync(@"
                SELECT id, entrega_id, estado_id, lat, lng, creado_en
                FROM entrega_eventos
                WHERE entrega_id = @e
                ORDER BY id DESC
                LIMIT 20;", new { e = entrega?.id ?? 0L });

            var steps = await db.QueryAsync(@"
                SELECT es.codigo, es.nombre, es.paso_publico
                FROM estados es
                JOIN procesos p ON p.id = es.proceso_id
                WHERE p.codigo='ORD' AND es.paso_publico IS NOT NULL
                ORDER BY es.paso_publico;");

            return Results.Ok(new { orden = ord, entrega, eventos, steps });
        }

        g.MapGet("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/tracking",
            (HttpContext ctx, string scope, string folio) =>
            {
                var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
                return TrackingHandler(ctx, local, folio);
            });

        g.MapPost("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/set-state",
            async (HttpContext ctx, string scope, string folio, SetStateReq body) =>
            {
                var code = (body.code ?? body.codigo)?.Trim();
                var note = (body.note ?? body.notas)?.Trim();

                if (string.IsNullOrWhiteSpace(code))
                    return Results.BadRequest(new { error = "code/codigo requerido" });

                var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
                using var db = OpenConn(ctx, local);
                using var tx = db.BeginTransaction();

                var ord = await db.QueryFirstOrDefaultAsync(@"
                    SELECT id, usuario_id, estado_actual_id, proceso_id
                    FROM ordenes
                    WHERE folio=@f
                    LIMIT 1;", new { f = folio }, tx);
                if (ord is null) return Results.NotFound(new { error = "Orden no encontrada" });

                long estadoDesdeId = (long)ord.estado_actual_id;

                var estadoHasta = await db.QueryFirstOrDefaultAsync(@"
                    SELECT id, codigo, nombre, paso_publico
                    FROM estados
                    WHERE proceso_id=@p AND UPPER(codigo)=UPPER(@c)
                    LIMIT 1;", new { p = (long)ord.proceso_id, c = code }, tx);
                if (estadoHasta is null) return Results.BadRequest(new { error = "Estado destino inválido" });

                long transicionId = await AsegurarTransicionAsync(db, tx, (long)ord.proceso_id, estadoDesdeId, (long)estadoHasta.id);

                await db.ExecuteAsync(@"
                    UPDATE ordenes
                    SET estado_actual_id=@e, actualizado_en=NOW()
                    WHERE id=@o;", new { e = (long)estadoHasta.id, o = (long)ord.id }, tx);

                var entrega = await db.QueryFirstOrDefaultAsync(
                    "SELECT id FROM entregas WHERE orden_id=@o LIMIT 1;",
                    new { o = (long)ord.id }, tx);
                long entregaId = entrega?.id ?? await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO entregas (orden_id, estado) VALUES (@o, 'pendiente');
                    SELECT LAST_INSERT_ID();", new { o = (long)ord.id }, tx);

                await db.ExecuteAsync(@"
                    INSERT INTO entrega_eventos (entrega_id, estado_id, lat, lng, creado_en)
                    VALUES (@en, @es, NULL, NULL, NOW());",
                    new { en = entregaId, es = (long)estadoHasta.id }, tx);

                await db.ExecuteAsync(@"
                    INSERT INTO historial_flujo (objeto_tipo, objeto_id, transicion_id, estado_id, usuario_id, notas, creado_en)
                    VALUES ('orden', @id, @tr, @st, @u, @n, NOW());",
                    new {
                        id = (long)ord.id,
                        tr = transicionId,
                        st = (long)estadoHasta.id,
                        u  = (long)ord.usuario_id,
                        n  = string.IsNullOrWhiteSpace(note) ? $"Cambio a {estadoHasta.codigo}" : note
                    }, tx);

                tx.Commit();
                return Results.Ok(new { ok = true });
            });

        g.MapPost("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/events",
            async (HttpContext ctx, string scope, string folio, AddEventReq body) =>
            {
                var note = (body.note ?? body.notas)?.Trim();
                var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);

                using var db = OpenConn(ctx, local);
                using var tx = db.BeginTransaction();

                var ord = await db.QueryFirstOrDefaultAsync(
                    "SELECT id, estado_actual_id FROM ordenes WHERE folio=@f LIMIT 1;", new { f = folio }, tx);
                if (ord is null) return Results.NotFound(new { error = "Orden no encontrada" });

                var entrega = await db.QueryFirstOrDefaultAsync(
                    "SELECT id FROM entregas WHERE orden_id=@o LIMIT 1;", new { o = (long)ord.id }, tx);
                long entregaId = entrega?.id ?? await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO entregas (orden_id, estado) VALUES (@o, 'pendiente');
                    SELECT LAST_INSERT_ID();", new { o = (long)ord.id }, tx);

                await db.ExecuteAsync(@"
                    INSERT INTO entrega_eventos (entrega_id, estado_id, lat, lng, creado_en)
                    VALUES (@en, @es, NULL, NULL, NOW());",
                    new { en = entregaId, es = (long)ord.estado_actual_id }, tx);

                long trId = await AsegurarTransicionAsync(
                    db, tx,
                    procesoId: await db.ExecuteScalarAsync<long>(
                        "SELECT proceso_id FROM ordenes WHERE id=@id;", new { id = (long)ord.id }, tx),
                    estadoDesdeId: (long)ord.estado_actual_id,
                    estadoHastaId: (long)ord.estado_actual_id);

                await db.ExecuteAsync(@"
                    INSERT INTO historial_flujo (objeto_tipo, objeto_id, transicion_id, estado_id, usuario_id, notas, creado_en)
                    VALUES ('orden', @id, @tr, @st, NULL, @n, NOW());",
                    new { id = (long)ord.id, tr = trId, st = (long)ord.estado_actual_id, n = string.IsNullOrWhiteSpace(note) ? "Evento manual" : note }, tx);

                tx.Commit();
                return Results.Ok(new { ok = true });
            });
    }

    private static async Task<string> NuevoFolioAsync(IDbConnection db, IDbTransaction tx)
    {
        var s = DateTime.UtcNow.ToString("yyyyMMdd");
        var rnd = Random.Shared.Next(1000, 9999);
        string folio = $"{s}-{rnd}";
        var exists = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM ordenes WHERE folio=@f;", new { f = folio }, tx);
        if (exists > 0) return await NuevoFolioAsync(db, tx);
        return folio;
    }

    private static async Task<long> AsegurarTransicionAsync(IDbConnection db, IDbTransaction tx, long procesoId, long estadoDesdeId, long estadoHastaId)
    {
        var tr = await db.QueryFirstOrDefaultAsync<long?>(@"
            SELECT id FROM transiciones
            WHERE proceso_id=@p AND estado_desde_id=@d AND estado_hasta_id=@h
            LIMIT 1;", new { p = procesoId, d = estadoDesdeId, h = estadoHastaId }, tx);

        if (tr is not null) return tr.Value;

        string codigo = $"SET-{estadoDesdeId}->{estadoHastaId}";
        string nombre = "Cambio de estado";
        var id = await db.ExecuteScalarAsync<long>(@"
            INSERT INTO transiciones (proceso_id, codigo, estado_desde_id, estado_hasta_id, nombre)
            VALUES (@p, @c, @d, @h, @n);
            SELECT LAST_INSERT_ID();",
            new { p = procesoId, c = codigo, d = estadoDesdeId, h = estadoHastaId, n = nombre }, tx);
        return id;
    }

    private static async Task<(long? pidA, long? pidB)> CopiarPersonalizacionesAsync(IDbConnection db, IDbTransaction tx, long fromCarritoItemId, long toOrdenItemId, long? fallbackDraftItemId = null)
    {
        var pers = (await db.QueryAsync<(long id, string lado)>(@"
            SELECT id, lado
            FROM personalizaciones
            WHERE propietario_tipo='carrito_item' AND propietario_id=@cid",
            new { cid = fromCarritoItemId }, tx)).ToList();

        if (pers.Count == 0 && fallbackDraftItemId.HasValue)
        {
            pers = (await db.QueryAsync<(long id, string lado)>(@"
                SELECT id, lado
                FROM personalizaciones
                WHERE propietario_tipo='carrito_item' AND propietario_id=@draftId",
                new { draftId = fallbackDraftItemId.Value }, tx)).ToList();
        }

        if (pers.Count > 0)
        {
            foreach (var (pid, _) in pers)
            {
                long newPid = await db.ExecuteScalarAsync<long>(@"
                    INSERT INTO personalizaciones (propietario_tipo, propietario_id, lado, captura)
                    SELECT 'orden_item', @oi, lado, captura
                    FROM personalizaciones WHERE id=@pid;
                    SELECT LAST_INSERT_ID();",
                    new { oi = toOrdenItemId, pid }, tx);

                await db.ExecuteAsync(@"
                    INSERT INTO personalizacion_capas
                    (personalizacion_id, tipo_capa, z_index, pos_x, pos_y, escala, rotacion, texto, fuente, color,
                     archivo_id, sticker_id, filtro_id, datos)
                    SELECT @nuevo, tipo_capa, z_index, pos_x, pos_y, escala, rotacion, texto, fuente, color,
                           archivo_id, sticker_id, filtro_id, datos
                    FROM personalizacion_capas
                    WHERE personalizacion_id=@old;",
                    new { nuevo = newPid, old = pid }, tx);
            }

            var lados = (await db.QueryAsync<(long id, string lado)>(@"
                SELECT id, lado
                FROM personalizaciones
                WHERE propietario_tipo='orden_item' AND propietario_id=@oi;",
                new { oi = toOrdenItemId }, tx)).ToList();

            long? pidA = lados.Where(x => string.Equals(x.lado, "A", StringComparison.OrdinalIgnoreCase)).Select(x => (long?)x.id).FirstOrDefault();
            long? pidB = lados.Where(x => string.Equals(x.lado, "B", StringComparison.OrdinalIgnoreCase)).Select(x => (long?)x.id).FirstOrDefault();

            await db.ExecuteAsync(@"
                UPDATE orden_items
                SET personalizacion_ladoA_id = @pa,
                    personalizacion_ladoB_id = @pb
                WHERE id=@oi;",
                new { pa = (object?)pidA ?? DBNull.Value, pb = (object?)pidB ?? DBNull.Value, oi = toOrdenItemId }, tx);

            return (pidA, pidB);
        }

        return (null, null);
    }

    private static async Task UpsertOrdenImagenAsync(IDbConnection db, IDbTransaction tx, long ordenId, long personalizacionId, char lado)
    {
        var archivoId = await db.ExecuteScalarAsync<long?>(@"
            SELECT pc.archivo_id
            FROM personalizacion_capas pc
            WHERE pc.personalizacion_id=@pid
              AND pc.tipo_capa='foto'
              AND pc.archivo_id IS NOT NULL
            ORDER BY COALESCE(pc.z_index,0) DESC, pc.id DESC
            LIMIT 1;",
            new { pid = personalizacionId }, tx);

        if (!archivoId.HasValue) return;

        await db.ExecuteAsync(@"
            INSERT INTO orden_imagenes (orden_id, archivo_id, lado)
            VALUES (@o, @a, @lado)
            ON DUPLICATE KEY UPDATE
              archivo_id = VALUES(archivo_id),
              creado_en  = CURRENT_TIMESTAMP;",
            new { o = ordenId, a = archivoId.Value, lado = (lado == 'B' ? "B" : "A") }, tx);

        await db.ExecuteAsync(@"
            UPDATE archivos
            SET propietario_tipo='orden', propietario_id=@o
            WHERE id=@a
              AND (propietario_tipo IS NULL OR propietario_tipo='personalizacion');",
            new { o = ordenId, a = archivoId.Value }, tx);
    }
}
