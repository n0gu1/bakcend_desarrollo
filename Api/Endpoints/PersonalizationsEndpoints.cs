// Api/Endpoints/PersonalizationsEndpoints.cs
using System;
using System.Data;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace Api.Endpoints;

public static class PersonalizationsEndpoints
{
    public static IEndpointRouteBuilder MapPersonalizationsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Personalizations");

        // Asegura/crea una personalización por (carrito_item, lado)
        g.MapPost("/{scope:regex((?i)^(local|default)$)}/personalizations", EnsurePersonalization);

        // Lista capas (join a personalizaciones para "lado" y a archivos para url)
        g.MapGet("/{scope:regex((?i)^(local|default)$)}/personalizations/{pid:long}/layers", GetLayers);

        // Fallback sin scope (redirige a "local" para mantener compatibilidad)
        g.MapGet("/personalizations/{pid:long}/layers", (long pid) =>
            Results.Redirect($"/api/local/personalizations/{pid}/layers", permanent: false));

        // Crea capa tipo foto (acepta archivo_id o url; si sólo hay url crea archivo)
        g.MapPost("/{scope:regex((?i)^(local|default)$)}/personalizations/{pid:long}/layers", CreateLayer);

        // Borrar capa
        g.MapPost("/{scope:regex((?i)^(local|default)$)}/personalizations/{pid:long}/layers/{layerId:long}/delete", DeleteLayer);

        return app;
    }

    /* ===== Helpers ===== */

    static MySqlConnection OpenConn(IConfiguration cfg, string scope)
    {
        var local = scope.Equals("local", StringComparison.OrdinalIgnoreCase);
        var cs =
            cfg.GetConnectionString(local ? "ComprasLocal" : "Default")
            ?? cfg.GetConnectionString("Default")
            ?? cfg.GetConnectionString("ComprasLocal")
            ?? throw new InvalidOperationException("Faltan ConnectionStrings:Default/ComprasLocal");
        return new MySqlConnection(cs);
    }

    /* ===== DTOs ===== */

    public record EnsureReq(long carritoItemId, string lado, long usuarioId);

    public record LayerReq(
        string  tipo_capa,               // 'foto' | 'texto' | 'sticker' | 'filtro'
        string? lado,                    // 'A' | 'B' (no se guarda en la tabla; se obtiene de personalizaciones)
        long?   archivo_id,
        string? url,                     // si no viene archivo_id pero sí url, creamos archivo
        int?    z_index,
        decimal? pos_x, decimal? pos_y,
        decimal? escala, decimal? rotacion,
        string? texto, string? fuente, string? color
    );

    /* ===== Endpoints ===== */

    // POST /api/{scope}/personalizations
    static async Task<IResult> EnsurePersonalization(
        [FromServices] IConfiguration cfg,
        string scope,
        [FromBody] EnsureReq body)
    {
        if (body.carritoItemId <= 0)
            return Results.BadRequest(new { message = "carritoItemId inválido" });

        var lado = (body.lado ?? "A").ToUpperInvariant() == "B" ? "B" : "A";

        await using var db = OpenConn(cfg, scope);
        await db.OpenAsync();

        // ¿Existe ya?
        var pid = await db.ExecuteScalarAsync<long?>(@"
            SELECT id
            FROM personalizaciones
            WHERE propietario_tipo='carrito_item' AND propietario_id=@cid AND lado=@lado
            ORDER BY id DESC
            LIMIT 1;",
            new { cid = body.carritoItemId, lado });

        if (pid is null)
        {
            // Nota: el dump define personalizaciones(propietario_tipo, propietario_id, lado, captura)
            pid = await db.ExecuteScalarAsync<long>(@"
                INSERT INTO personalizaciones (propietario_tipo, propietario_id, lado, captura)
                VALUES ('carrito_item', @cid, @lado, NULL);
                SELECT LAST_INSERT_ID();",
                new { cid = body.carritoItemId, lado });
        }

        return Results.Ok(new { id = pid, lado });
    }

    // GET /api/{scope}/personalizations/{pid}/layers
    static async Task<IResult> GetLayers(
        [FromServices] IConfiguration cfg,
        string scope,
        long pid)
    {
        await using var db = OpenConn(cfg, scope);
        await db.OpenAsync();

        // Importante: la tabla `personalizacion_capas` no tiene columna `lado`,
        // se toma de `personalizaciones.lado`.
        var rows = await db.QueryAsync(@"
            SELECT
              c.id,
              c.personalizacion_id,
              p.lado AS lado,
              c.tipo_capa,
              c.z_index,
              c.pos_x, c.pos_y,
              c.escala, c.rotacion,
              c.texto, c.fuente, c.color,
              c.archivo_id,
              a.ruta AS archivo_url
            FROM personalizacion_capas c
            JOIN personalizaciones p ON p.id = c.personalizacion_id
            LEFT JOIN archivos a ON a.id = c.archivo_id
            WHERE c.personalizacion_id = @pid
            ORDER BY COALESCE(c.z_index,0) DESC, c.id DESC;",
            new { pid });

        return Results.Ok(rows);
    }

    // POST /api/{scope}/personalizations/{pid}/layers
    static async Task<IResult> CreateLayer(
        [FromServices] IConfiguration cfg,
        string scope,
        long pid,
        [FromBody] LayerReq body)
    {
        if (string.IsNullOrWhiteSpace(body.tipo_capa))
            return Results.BadRequest(new { message = "tipo_capa requerido" });

        var tipoCapa = body.tipo_capa.Trim().ToLowerInvariant();
        if (tipoCapa is not ("foto" or "texto" or "sticker" or "filtro"))
            return Results.BadRequest(new { message = "tipo_capa inválido" });

        await using var db = OpenConn(cfg, scope);
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        // Aseguramos que la personalización existe y obtenemos el lado
        var lado = await db.ExecuteScalarAsync<string?>(@"
            SELECT lado FROM personalizaciones WHERE id=@pid LIMIT 1;", new { pid }, tx);
        if (string.IsNullOrWhiteSpace(lado))
            return Results.BadRequest(new { message = "personalización no encontrada" });

        long? archivoId = body.archivo_id;

        // Si no viene archivo_id pero sí url, creamos el archivo.
        if (archivoId is null && !string.IsNullOrWhiteSpace(body.url))
        {
            // Mapear tipo_capa -> archivos.tipo
            // 'foto' -> 'foto', 'sticker' -> 'sticker', 'filtro'/'texto' -> 'plantilla'
            var tipoArchivo = tipoCapa switch
            {
                "foto" => "foto",
                "sticker" => "sticker",
                _ => "plantilla"
            };

            archivoId = await db.ExecuteScalarAsync<long>(@"
                INSERT INTO archivos (tipo, ruta, mime, creado_en)
                VALUES (@tipo, @ruta, NULL, NOW());
                SELECT LAST_INSERT_ID();",
                new { tipo = tipoArchivo, ruta = body.url }, tx);
        }

        // Para fotos, debemos tener archivoId
        if (tipoCapa == "foto" && archivoId is null)
            return Results.BadRequest(new { message = "archivo_id o url requerido para tipo_capa='foto'" });

        // Insertar capa (NOTA: la tabla no tiene columna `lado`; lo inferimos de `personalizaciones`)
        var layerId = await db.ExecuteScalarAsync<long>(@"
            INSERT INTO personalizacion_capas
                (personalizacion_id, tipo_capa, z_index, pos_x, pos_y, escala, rotacion, texto, fuente, color, archivo_id)
            VALUES
                (@pid, @tipo, @zi, @x, @y, @esc, @rot, @txt, @font, @clr, @aid);
            SELECT LAST_INSERT_ID();",
            new
            {
                pid,
                tipo = tipoCapa,
                zi = body.z_index ?? 5,
                x = body.pos_x ?? 0.5m,
                y = body.pos_y ?? 0.58m,
                esc = body.escala ?? 1m,
                rot = body.rotacion ?? 0m,
                txt = body.texto,
                font = body.fuente,
                clr = body.color,
                aid = archivoId
            }, tx);

        // Marcar el archivo como perteneciente a esta personalización (opcional)
        if (archivoId is not null)
        {
            await db.ExecuteAsync(@"
                UPDATE archivos
                SET propietario_tipo='personalizacion', propietario_id=@pid
                WHERE id=@aid
                  AND (propietario_tipo IS NULL OR propietario_tipo='personalizacion');",
                new { pid, aid = archivoId.Value }, tx);
        }

        await tx.CommitAsync();

        // Devolver la fila con joins y "lado" desde personalizaciones
        var row = await db.QueryFirstAsync(@"
            SELECT
              c.id,
              c.personalizacion_id,
              p.lado AS lado,
              c.tipo_capa,
              c.z_index,
              c.pos_x, c.pos_y,
              c.escala, c.rotacion,
              c.texto, c.fuente, c.color,
              c.archivo_id,
              a.ruta AS archivo_url
            FROM personalizacion_capas c
            JOIN personalizaciones p ON p.id = c.personalizacion_id
            LEFT JOIN archivos a ON a.id = c.archivo_id
            WHERE c.id = @id;", new { id = layerId });

        return Results.Ok(row);
    }

    // POST /api/{scope}/personalizations/{pid}/layers/{layerId}/delete
    static async Task<IResult> DeleteLayer(
        [FromServices] IConfiguration cfg,
        string scope,
        long pid,
        long layerId)
    {
        await using var db = OpenConn(cfg, scope);
        await db.OpenAsync();

        var n = await db.ExecuteAsync(@"
            DELETE FROM personalizacion_capas
            WHERE id=@id AND personalizacion_id=@pid;",
            new { id = layerId, pid });

        return Results.Ok(new { ok = n > 0 });
    }
}
