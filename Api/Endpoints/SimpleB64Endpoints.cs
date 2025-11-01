// BaseUsuarios.Api/Endpoints/SimpleB64Endpoints.cs
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints;

public static class SimpleB64Endpoints
{
    // id: opcional (si viene => UPDATE; si no => INSERT)
    // lado: "A" | "B"
    // dataUrl: "data:image/png;base64,..."
    // usuarioId: REQUERIDO para poder enlazar a la orden automáticamente
    // ordenId / ordenItemId: opcionales (se respetan FKs si existen)
    public record SaveReq(long? id, string lado, string dataUrl, long? usuarioId, long? ordenId, long? ordenItemId);
    public record SaveResp(long id);

    public static void MapSimpleB64Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/imagenes-b64", SaveImagenB64);
        app.MapPost("/api/imagenes-b64/{id:long}/link-order", LinkToOrder);
        app.MapGet("/api/imagenes-b64/ultimos", GetUltimos);
        app.MapGet("/api/imagenes-b64/by-order/{ordenId:long}", GetByOrder);
    }

    /* =================== Handlers =================== */

    static async Task<IResult> SaveImagenB64(SaveReq req, IConfiguration cfg)
    {
        var lado = (req.lado ?? "").Trim().ToUpperInvariant();
        if (lado != "A" && lado != "B") return Results.BadRequest("lado debe ser 'A' o 'B'");
        if (string.IsNullOrWhiteSpace(req.dataUrl)) return Results.BadRequest("dataUrl vacío");
        if (req.usuarioId is null or <= 0) return Results.BadRequest("usuarioId requerido");

        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");

        await using var cn = new MySqlConnection(cs);

        if (req.id is null or <= 0)
        {
            // INSERT: guarda usuario_id y el lado correspondiente
            const string sqlIns = @"
INSERT INTO imagenes_b64 (usuario_id, orden_id, orden_item_id, ladoA_b64, ladoB_b64, created_at)
VALUES (@usuarioId, @ordenId, @ordenItemId, @a, @b, NOW());
SELECT LAST_INSERT_ID();";

            var newId = await cn.ExecuteScalarAsync<long>(sqlIns, new
            {
                usuarioId = req.usuarioId,
                ordenId = req.ordenId,
                ordenItemId = req.ordenItemId,
                a = (lado == "A") ? req.dataUrl : null,
                b = (lado == "B") ? req.dataUrl : null
            });

            return Results.Ok(new SaveResp(newId));
        }
        else
        {
            // UPDATE: completa el par y opcionalmente asocia orden / item
            const string sqlUpd = @"
UPDATE imagenes_b64
SET
  usuario_id    = COALESCE(@usuarioId, usuario_id),
  ladoA_b64     = CASE WHEN @lado = 'A' THEN @dataUrl ELSE ladoA_b64 END,
  ladoB_b64     = CASE WHEN @lado = 'B' THEN @dataUrl ELSE ladoB_b64 END,
  orden_id      = COALESCE(@ordenId, orden_id),
  orden_item_id = COALESCE(@ordenItemId, orden_item_id)
WHERE id = @id;
SELECT @id;";

            var id = await cn.ExecuteScalarAsync<long>(sqlUpd, new
            {
                id = req.id,
                lado,
                dataUrl = req.dataUrl,
                usuarioId = req.usuarioId,
                ordenId = req.ordenId,
                ordenItemId = req.ordenItemId
            });

            return Results.Ok(new SaveResp(id));
        }
    }

    // Vincula una fila existente a una orden (si aún no tenía orden_id)
    public record LinkReq(long ordenId, long? ordenItemId);
    static async Task<IResult> LinkToOrder(long id, LinkReq req, IConfiguration cfg)
    {
        if (req.ordenId <= 0) return Results.BadRequest("ordenId requerido");

        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");

        const string sql = @"
UPDATE imagenes_b64
SET orden_id = @ordenId,
    orden_item_id = COALESCE(@ordenItemId, orden_item_id)
WHERE id = @id;";

        await using var cn = new MySqlConnection(cs);
        var rows = await cn.ExecuteAsync(sql, new { id, req.ordenId, req.ordenItemId });
        if (rows == 0) return Results.NotFound(new { message = "fila no encontrada" });

        return Results.Ok(new { ok = true, id, req.ordenId, req.ordenItemId });
    }

    static async Task<IResult> GetUltimos(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
        const string sql = @"
SELECT id, usuario_id, orden_id, orden_item_id,
       LENGTH(ladoA_b64) AS bytes_A,
       LENGTH(ladoB_b64) AS bytes_B,
       created_at
FROM imagenes_b64
ORDER BY id DESC
LIMIT 50;";
        await using var cn = new MySqlConnection(cs);
        var rows = await cn.QueryAsync(sql);
        return Results.Ok(rows);
    }

    // Último par por orden (si hay varios, trae el más reciente)
    static async Task<IResult> GetByOrder(long ordenId, IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
        const string sql = @"
SELECT id, usuario_id, ladoA_b64, ladoB_b64, created_at
FROM imagenes_b64
WHERE orden_id = @ordenId
ORDER BY id DESC
LIMIT 1;";
        await using var cn = new MySqlConnection(cs);
        var row = await cn.QueryFirstOrDefaultAsync(sql, new { ordenId });
        return Results.Ok(row);
    }
}
