// Api/Endpoints/UploadsEndpoints.cs
using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace Api.Endpoints;

public static class UploadsEndpoints
{
    public static IEndpointRouteBuilder MapUploadsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api").WithTags("Uploads");

        // Soporta ambos estilos:
        //  - /api/local/uploads   (usa CS ComprasLocal)
        //  - /api/default/uploads (usa CS Default)
        g.MapPost("/{scope:regex((?i)^(local|default)$)}/uploads", UploadAsync);

        // Fallback: /api/uploads  -> intenta Default y si no existe, ComprasLocal
        g.MapPost("/uploads", UploadAsync);

        return app;
    }

    private static async Task<IResult> UploadAsync(HttpContext ctx, IConfiguration cfg)
    {
        var req = ctx.Request;

        if (!req.HasFormContentType)
            return Results.BadRequest(new { message = "form-data requerido" });

        var form = await req.ReadFormAsync();

        var file = form.Files.GetFile("file");
        if (file == null || file.Length == 0)
            return Results.BadRequest(new { message = "file requerido" });

        // ====== Metadata opcional que manda el front ======
        // - folio: para guardar en /uploads/orders/{folio}
        // - personalizationId: si viene, marcamos el archivo como de esa personalización
        // - lado: (A|B) solo informativo; no se guarda aquí
        var folio = form.TryGetValue("folio", out var _folio) ? _folio.ToString().Trim() : null;
        long? personalizationId = null;
        if (form.TryGetValue("personalizationId", out var _pid) && long.TryParse(_pid.ToString(), out var pidParsed))
            personalizationId = pidParsed;
        var lado = form.TryGetValue("lado", out var _lado) ? _lado.ToString().Trim().ToUpperInvariant() : null;

        // ====== Elegir conexión según scope ======
        var scopeRaw = ctx.GetRouteValue("scope")?.ToString() ?? "default";
        var local = scopeRaw.Equals("local", StringComparison.OrdinalIgnoreCase);
        var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")
                 ?? cfg.GetConnectionString("Default")
                 ?? cfg.GetConnectionString("ComprasLocal")
                 ?? throw new InvalidOperationException("Faltan ConnectionStrings:Default/ComprasLocal");

        // ====== Guardar en disco ======
        // Carpeta: /wwwroot/uploads/{yyyy}/{MM}  ó  /wwwroot/uploads/orders/{folio}
        var today = DateTime.UtcNow;
        var baseDir = string.IsNullOrWhiteSpace(folio)
            ? Path.Combine("wwwroot", "uploads", today.ToString("yyyy"), today.ToString("MM"))
            : Path.Combine("wwwroot", "uploads", "orders", folio);

        Directory.CreateDirectory(baseDir);

        var safeExt = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(safeExt)) safeExt = ".bin";
        var newName = $"{Guid.NewGuid():N}{safeExt}";
        var diskPath = Path.Combine(baseDir, newName);

        await using (var fs = new FileStream(diskPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await file.CopyToAsync(fs);
        }

        // Ruta pública (comienza con /)
        var rel = Path.GetRelativePath("wwwroot", diskPath).Replace('\\', '/');
        var url = "/" + rel;

        // Mime (preferimos el ContentType si viene)
        var mime = !string.IsNullOrWhiteSpace(file.ContentType) ? file.ContentType : "application/octet-stream";

        // ====== Registrar en BD (tabla archivos) ======
        await using var db = new MySqlConnection(cs);
        await db.OpenAsync();

        // Si el front envió personalizationId, marcamos propietario_tipo='personalizacion'
        string? propietarioTipo = personalizationId.HasValue ? "personalizacion" : null;

        // ⚠️ IMPORTANTE: forzar tipo object en el operador condicional
        object propietarioIdObj = personalizationId.HasValue ? (object)personalizationId.Value : DBNull.Value;
        object propietarioTipoObj = (object?)propietarioTipo ?? DBNull.Value;

        // NOTA: En la BD, 'tipo' es NOT NULL; usamos 'foto' por defecto.
        // Columnas típicas: id, tipo, ruta, mime, ancho, alto, bytes, propietario_tipo, propietario_id, creado_en
        var sql = @"
            INSERT INTO archivos (tipo, ruta, mime, bytes, propietario_tipo, propietario_id, creado_en)
            VALUES ('foto', @r, @m, @bytes, @pt, @pid, NOW());
            SELECT LAST_INSERT_ID();";

        long archivoId = await db.ExecuteScalarAsync<long>(sql, new
        {
            r = url,
            m = mime,
            bytes = (int?)file.Length,
            pt = propietarioTipoObj,
            pid = propietarioIdObj
        });

        // Respuesta ampliada (compatible con el front actual)
        return Results.Ok(new
        {
            ok = true,
            scope = local ? "local" : "default",
            archivoId,
            url,
            name = newName,
            size = file.Length,
            mime,
            folio = folio ?? string.Empty,
            personalizationId = personalizationId ?? 0,
            lado = lado ?? string.Empty
        });
    }
}
