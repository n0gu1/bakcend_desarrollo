using System.Data;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints;

public static class OrderHistoryEndpoints
{
    public static void MapOrderHistoryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api");

        // GET /api/{scope}/orders/history?usuarioId=226&page=1&pageSize=20
        g.MapGet("/{scope:regex((?i)^(local|default)$)}/orders/history", async (
            HttpContext ctx, string scope, long usuarioId, int page = 1, int pageSize = 20) =>
        {
            if (usuarioId <= 0) return Results.BadRequest(new { error = "usuarioId requerido" });
            if (page <= 0) page = 1;
            if (pageSize <= 0 || pageSize > 100) pageSize = 20;

            bool local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);

            using var db = OpenConn(ctx, local);

            var totalOrders = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM ordenes WHERE usuario_id=@u;", new { u = usuarioId });

            var orders = (await db.QueryAsync(@"
                SELECT o.id, o.folio, o.total, o.creado_en,
                       es.codigo AS estado_codigo, es.nombre AS estado_nombre
                FROM ordenes o
                LEFT JOIN estados es ON es.id = o.estado_actual_id
                WHERE o.usuario_id=@u
                ORDER BY o.creado_en DESC, o.id DESC
                LIMIT @skip, @take;",
                new { u = usuarioId, skip = (page - 1) * pageSize, take = pageSize }))
                .ToList();

            if (orders.Count == 0)
                return Results.Ok(new { total = totalOrders, page, pageSize, orders, items = Array.Empty<object>() });

            var orderIds = orders.Select(o => (long)o.id).ToArray();

            var items = await db.QueryAsync(@"
                SELECT oi.id, oi.orden_id, oi.producto_id, p.nombre AS producto_nombre,
                       oi.cantidad, oi.precio_unitario,
                       (oi.cantidad * oi.precio_unitario) AS subtotal
                FROM orden_items oi
                JOIN productos p ON p.id = oi.producto_id
                WHERE oi.orden_id IN @ids
                ORDER BY oi.orden_id DESC, oi.id ASC;",
                new { ids = orderIds });

            return Results.Ok(new
            {
                total = totalOrders,
                page,
                pageSize,
                orders,
                items
            });
        });
    }

    // Helper local a este archivo; si prefieres, muévelo a una clase compartida.
    private static IDbConnection OpenConn(HttpContext ctx, bool local)
    {
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")!;
        var conn = new MySqlConnection(cs);
        conn.Open();
        return conn;
    }
}
