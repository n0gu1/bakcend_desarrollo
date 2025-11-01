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
    // lado: "A" | "B" (opcional si solo envías urlGeneral)
    // dataUrl: "data:image/png;base64,..." (opcional si solo envías urlGeneral)
    // urlGeneral: URL sin lado (opcional; si viene, se inserta/actualiza url_general)
    // usuarioId: REQUERIDO (para crear o actualizar)
    // ordenId / ordenItemId: opcionales (se respetan FKs si existen)
    public record SaveReq(
        long? id,
        string? lado,
        string? dataUrl,
        long? usuarioId,
        long? ordenId,
        long? ordenItemId,
        string? urlGeneral
    );

    public record SaveResp(long id);

    public static void MapSimpleB64Endpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/imagenes-b64", SaveImagenB64);
        app.MapPost("/api/imagenes-b64/{id:long}/link-order", LinkToOrder);
        app.MapPost("/api/imagenes-b64/{id:long}/url-general", SetUrlGeneral); // <-- NUEVO endpoint dedicado
        app.MapGet("/api/imagenes-b64/ultimos", GetUltimos);
        app.MapGet("/api/imagenes-b64/by-order/{ordenId:long}", GetByOrder);
        app.MapGet("/api/imagenes-b64/{id:long}", GetById);
    }

    /* =================== Handlers =================== */

    static async Task<IResult> SaveImagenB64(SaveReq req, IConfiguration cfg)
    {
        if (req.usuarioId is null or <= 0)
            return Results.BadRequest("usuarioId requerido");

        var lado = (req.lado ?? "").Trim().ToUpperInvariant();
        var hasDataUrl = !string.IsNullOrWhiteSpace(req.dataUrl);
        var hasValidSide = lado is "A" or "B";
        var hasB64Payload = hasDataUrl && hasValidSide;

        // Saber si el caller envió (o no) el parámetro urlGeneral (aunque sea vacío)
        var urlParamProvided = req.urlGeneral is not null;
        // Normalizamos: "" o espacios => NULL (borra valor)
        string? urlToSet = urlParamProvided
            ? (string.IsNullOrWhiteSpace(req.urlGeneral) ? null : req.urlGeneral!.Trim())
            : null;

        if (!hasB64Payload && !urlParamProvided)
            return Results.BadRequest("Nada que guardar: envía (lado + dataUrl) y/o urlGeneral.");

        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");

        await using var cn = new MySqlConnection(cs);

        if (req.id is null or <= 0)
        {
            // INSERT: permite crear la fila con solo url_general, solo A/B, o ambos.
            const string sqlIns = @"
INSERT INTO imagenes_b64 (usuario_id, orden_id, orden_item_id, ladoA_b64, ladoB_b64, url_general, created_at)
VALUES (@usuarioId, @ordenId, @ordenItemId, @a, @b, @url, NOW());
SELECT LAST_INSERT_ID();";

            var newId = await cn.ExecuteScalarAsync<long>(sqlIns, new
            {
                usuarioId   = req.usuarioId,
                ordenId     = req.ordenId,
                ordenItemId = req.ordenItemId,
                a           = hasB64Payload && lado == "A" ? req.dataUrl : null,
                b           = hasB64Payload && lado == "B" ? req.dataUrl : null,
                url         = urlToSet
            });

            return Results.Ok(new SaveResp(newId));
        }
        else
        {
            // UPDATE: solo actualiza lo que vino (lado A/B y/o url_general).
            var set = new List<string>
            {
                "usuario_id = COALESCE(@usuarioId, usuario_id)",
                "orden_id = COALESCE(@ordenId, orden_id)",
                "orden_item_id = COALESCE(@ordenItemId, orden_item_id)"
            };

            if (hasB64Payload)
            {
                set.Add("ladoA_b64 = CASE WHEN @lado = 'A' THEN @dataUrl ELSE ladoA_b64 END");
                set.Add("ladoB_b64 = CASE WHEN @lado = 'B' THEN @dataUrl ELSE ladoB_b64 END");
            }

            if (urlParamProvided)
            {
                // Esto permite tanto setear como borrar (NULL) url_general
                set.Add("url_general = @urlToSet");
            }

            var sqlUpd = $@"
UPDATE imagenes_b64
SET {string.Join(", ", set)}
WHERE id = @id;
SELECT @id;";

            var id = await cn.ExecuteScalarAsync<long>(sqlUpd, new
            {
                id        = req.id,
                lado,
                dataUrl   = req.dataUrl,
                usuarioId = req.usuarioId,
                ordenId   = req.ordenId,
                ordenItemId = req.ordenItemId,
                urlToSet
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

    // NUEVO: setear únicamente url_general (sin tocar lados)
    public record UrlGeneralReq(string urlGeneral);
    static async Task<IResult> SetUrlGeneral(long id, UrlGeneralReq body, IConfiguration cfg)
    {
        if (body is null) return Results.BadRequest(new { message = "payload requerido" });

        // Normaliza: vacío/espacios => NULL
        string? url = string.IsNullOrWhiteSpace(body.urlGeneral) ? null : body.urlGeneral.Trim();

        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");

        const string sql = @"UPDATE imagenes_b64 SET url_general = @u WHERE id = @id;";
        await using var cn = new MySqlConnection(cs);
        var rows = await cn.ExecuteAsync(sql, new { id, u = url });
        if (rows == 0) return Results.NotFound(new { message = "fila no encontrada" });

        return Results.Ok(new { ok = true, id, url_general = url });
    }

    static async Task<IResult> GetUltimos(IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
        const string sql = @"
SELECT id, usuario_id, orden_id, orden_item_id,
       LENGTH(ladoA_b64) AS bytes_A,
       LENGTH(ladoB_b64) AS bytes_B,
       url_general,
       created_at
FROM imagenes_b64
ORDER BY id DESC
LIMIT 50;";
        await using var cn = new MySqlConnection(cs);
        var rows = await cn.QueryAsync(sql);
        return Results.Ok(rows);
    }

    // Último par por orden (si hay varios, trae el más reciente) — incluye url_general
    static async Task<IResult> GetByOrder(long ordenId, IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
        const string sql = @"
SELECT id, usuario_id, ladoA_b64, ladoB_b64, url_general, created_at
FROM imagenes_b64
WHERE orden_id = @ordenId
ORDER BY id DESC
LIMIT 1;";
        await using var cn = new MySqlConnection(cs);
        var row = await cn.QueryFirstOrDefaultAsync(sql, new { ordenId });
        return Results.Ok(row);
    }

    // Obtener por id — incluye url_general
    static async Task<IResult> GetById(long id, IConfiguration cfg)
    {
        var cs = cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
        const string sql = @"
SELECT id, usuario_id, orden_id, orden_item_id, ladoA_b64, ladoB_b64, url_general, created_at
FROM imagenes_b64
WHERE id = @id
LIMIT 1;";
        await using var cn = new MySqlConnection(cs);
        var row = await cn.QueryFirstOrDefaultAsync(sql, new { id });
        return row is null ? Results.NotFound(new { message = "no existe" }) : Results.Ok(row);
    }
}
