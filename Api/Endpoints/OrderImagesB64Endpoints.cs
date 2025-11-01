// BaseUsuarios.Api/Endpoints/OrderImagesB64Endpoints.cs
using System;
using System.Data;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class OrderImagesB64Endpoints
    {
        private const string VersionTag = "oib64-2025-10-31-ForceComprasLocal";

        private sealed record Oib64Row(
            long id,
            long? orden_id,
            long? usuario_id,
            string folio,
            string? mime_a,
            string? mime_b,
            string? lado_a_b64,
            string? lado_b_b64,
            DateTime creado_en,
            DateTime? actualizado_en
        );

        public static IEndpointRouteBuilder MapOrderImagesB64Endpoints(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/api").WithTags("OrderImagesB64");

            g.MapGet("/diag/oib64", () => Results.Ok(new { ok = true, version = VersionTag }));

            // META
            g.MapGet("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/images-b64/meta",
                (HttpContext ctx, IConfiguration cfg, string scope, string folio) => MetaAsync(ctx, cfg, folio));

            // A/B
            g.MapGet("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/images-b64/a",
                (HttpContext ctx, IConfiguration cfg, string scope, string folio) => SideAsync(ctx, cfg, folio, 'A'));

            g.MapGet("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/images-b64/b",
                (HttpContext ctx, IConfiguration cfg, string scope, string folio) => SideAsync(ctx, cfg, folio, 'B'));

            // REBUILD (+ copia opcional a columnas nuevas de `ordenes`)
            g.MapPost("/{scope:regex((?i)^(local|default)$)}/orders/{folio}/images-b64/rebuild",
                (HttpContext ctx, IConfiguration cfg, string scope, string folio) => ForceBuildAsync(ctx, cfg, folio));

            return app;
        }

        /* ========================= META ========================= */
        private static async Task<IResult> MetaAsync(HttpContext ctx, IConfiguration cfg, string folio)
        {
            await using var db = OpenConn(cfg); // SIEMPRE ComprasLocal
            var row = await db.QueryFirstOrDefaultAsync<Oib64Row?>(@"
                SELECT id, orden_id, usuario_id, folio, mime_a, mime_b,
                       lado_a_b64, lado_b_b64, creado_en, actualizado_en
                FROM orden_imagenes_b64
                WHERE folio=@f LIMIT 1;", new { f = folio });

            if (row is null)
            {
                var built = await BuildIfPossibleAsync(ctx, db, folio, force: false);
                if (built is null) return Results.NotFound(new { message = "Sin registro para ese folio" });
                row = built;
            }

            return Results.Ok(new
            {
                folio = row.folio,
                mimeA = row.mime_a,
                mimeB = row.mime_b,
                lenA  = row.lado_a_b64?.Length ?? 0,
                lenB  = row.lado_b_b64?.Length ?? 0,
                row.creado_en,
                row.actualizado_en
            });
        }

        /* ============== REBUILD + (opcional) COPIA A ORDENES ============== */
        private static async Task<IResult> ForceBuildAsync(HttpContext ctx, IConfiguration cfg, string folio)
        {
            await using var db = OpenConn(cfg); // SIEMPRE ComprasLocal

            var built = await BuildIfPossibleAsync(ctx, db, folio, force: true);
            if (built is null) return Results.NotFound(new { message = "No hay imágenes encontradas para ese folio" });

            // ¿Copiar a columnas nuevas de `ordenes`?
            if (ctx.Request.Query.ContainsKey("copyToOrdenes"))
            {
                await TryCopyToOrdenesColumnsAsync(db, built);
            }

            return Results.Ok(new
            {
                ok = true,
                rebuilt = true,
                folio = built.folio,
                mimeA = built.mime_a,
                mimeB = built.mime_b,
                lenA  = built.lado_a_b64?.Length ?? 0,
                lenB  = built.lado_b_b64?.Length ?? 0,
                built.creado_en,
                built.actualizado_en
            });
        }

        /* ======================= Helpers ======================= */

        // **FORZADO**: SIEMPRE usa ConnectionStrings:ComprasLocal
        private static MySqlConnection OpenConn(IConfiguration cfg)
        {
            var cs = cfg.GetConnectionString("ComprasLocal")
                     ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
            var conn = new MySqlConnection(cs);
            conn.Open();
            return conn;
        }

        // Lee del disco wwwroot/<ruta>; si no existe, intenta GET http(s)://<host>/<ruta>
        private static async Task<string?> ReadAsDataUrlAsync(HttpContext ctx, string? publicPath, string fallbackMime)
        {
            if (string.IsNullOrWhiteSpace(publicPath)) return null;

            // 1) Disco
            var disk = Path.Combine("wwwroot", publicPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(disk))
            {
                var bytes = await File.ReadAllBytesAsync(disk);
                var b64 = Convert.ToBase64String(bytes);
                var mime = string.IsNullOrWhiteSpace(fallbackMime) ? "image/jpeg" : fallbackMime;
                return $"data:{mime};base64,{b64}";
            }

            // 2) HTTP al mismo host
            try
            {
                var req = ctx.Request;
                var baseUrl = $"{req.Scheme}://{req.Host.Value}";
                var url = publicPath.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? publicPath
                        : $"{baseUrl}/{publicPath.TrimStart('/')}";
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var bytes = await http.GetByteArrayAsync(url);
                var b64 = Convert.ToBase64String(bytes);
                var mime = string.IsNullOrWhiteSpace(fallbackMime) ? "image/jpeg" : fallbackMime;
                return $"data:{mime};base64,{b64}";
            }
            catch
            {
                return null;
            }
        }

        /// Pobla `orden_imagenes_b64` desde `orden_imagenes` o `personalizacion_capas`.
        /// force=false => rellena faltantes; force=true => reescribe A y/o B si hay nuevo material.
        private static async Task<Oib64Row?> BuildIfPossibleAsync(HttpContext ctx, IDbConnection db, string folio, bool force)
        {
            // 1) Resolver orden
            var ord = await db.QueryFirstOrDefaultAsync<(long id, long usuario_id)?>(@"
                SELECT o.id, o.usuario_id
                FROM ordenes o
                WHERE o.folio=@f LIMIT 1;", new { f = folio });
            if (ord is null) return null;

            long ordenId = ord.Value.id;
            long usuarioId = ord.Value.usuario_id;

            // 2) Buscar A/B en `orden_imagenes`
            var ab = (await db.QueryAsync<(string lado, long archivo_id)?>(@"
                SELECT lado, archivo_id
                FROM orden_imagenes
                WHERE orden_id=@o;", new { o = ordenId }))
                .Where(x => x.HasValue)
                .Select(x => x!.Value);

            long? aId = null, bId = null;
            foreach (var (lado, archivo_id) in ab)
            {
                var L = (lado ?? "").Trim().ToUpperInvariant();
                if (L == "A") aId = archivo_id;
                if (L == "B") bId = archivo_id;
            }

            // 3) Si falta, buscar en personalización (primera capa 'foto' con archivo)
            if (aId is null)
            {
                aId = await db.ExecuteScalarAsync<long?>(@"
                    SELECT pc.archivo_id
                    FROM orden_items oi
                    JOIN personalizacion_capas pc
                      ON pc.personalizacion_id = oi.personalizacion_ladoA_id
                    WHERE oi.orden_id=@o AND pc.tipo_capa='foto' AND pc.archivo_id IS NOT NULL
                    ORDER BY COALESCE(pc.z_index,0) DESC, pc.id DESC
                    LIMIT 1;", new { o = ordenId });
            }
            if (bId is null)
            {
                bId = await db.ExecuteScalarAsync<long?>(@"
                    SELECT pc.archivo_id
                    FROM orden_items oi
                    JOIN personalizacion_capas pc
                      ON pc.personalizacion_id = oi.personalizacion_ladoB_id
                    WHERE oi.orden_id=@o AND pc.tipo_capa='foto' AND pc.archivo_id IS NOT NULL
                    ORDER BY COALESCE(pc.z_index,0) DESC, pc.id DESC
                    LIMIT 1;", new { o = ordenId });
            }

            if (aId is null && bId is null) return null;

            // 4) Resolver ruta/mime
            async Task<(string? ruta, string? mime)> RutaMime(long? id)
            {
                if (id is null) return (null, null);
                var rm = await db.QueryFirstOrDefaultAsync<(string ruta, string? mime)?>(@"
                    SELECT ruta, mime FROM archivos WHERE id=@id LIMIT 1;", new { id });
                return rm is null ? (null, null) : (rm.Value.ruta, rm.Value.mime);
            }

            var (rutaA, mimeA) = await RutaMime(aId);
            var (rutaB, mimeB) = await RutaMime(bId);

            // 5) Leer/convertir (disco o HTTP)
            var dataUrlA = await ReadAsDataUrlAsync(ctx, rutaA, mimeA ?? "image/jpeg");
            var dataUrlB = await ReadAsDataUrlAsync(ctx, rutaB, mimeB ?? "image/jpeg");

            if (dataUrlA is null && dataUrlB is null) return null;

            // 6) Upsert en orden_imagenes_b64
            var existing = await db.QueryFirstOrDefaultAsync<(long id, string? la, string? lb)?>(@"
                SELECT id, lado_a_b64, lado_b_b64
                FROM orden_imagenes_b64
                WHERE folio=@f LIMIT 1;", new { f = folio });

            if (existing is null)
            {
                await db.ExecuteAsync(@"
                    INSERT INTO orden_imagenes_b64
                      (orden_id, usuario_id, folio, mime_a, mime_b,
                       lado_a_b64, lado_b_b64, creado_en, actualizado_en)
                    VALUES
                      (@o, @u, @f, @ma, @mb, @ba, @bb, NOW(), NOW());",
                    new
                    {
                        o = ordenId,
                        u = usuarioId,
                        f = folio,
                        ma = mimeA ?? "image/jpeg",
                        mb = mimeB ?? "image/jpeg",
                        ba = (object?)dataUrlA ?? DBNull.Value,
                        bb = (object?)dataUrlB ?? DBNull.Value
                    });
            }
            else
            {
                var setA = (force || existing.Value.la is null) && dataUrlA is not null
                    ? "lado_a_b64=@ba,"
                    : "";
                var setB = (force || existing.Value.lb is null) && dataUrlB is not null
                    ? "lado_b_b64=@bb,"
                    : "";

                var sql = $@"
                    UPDATE orden_imagenes_b64
                    SET mime_a=@ma, mime_b=@mb,
                        {setA} {setB}
                        usuario_id=@u, actualizado_en=NOW()
                    WHERE folio=@f;";
                sql = sql.Replace("SET ,", "SET ").Replace(",  WHERE", " WHERE");

                await db.ExecuteAsync(sql, new
                {
                    u = usuarioId,
                    f = folio,
                    ma = mimeA ?? "image/jpeg",
                    mb = mimeB ?? "image/jpeg",
                    ba = dataUrlA,
                    bb = dataUrlB
                });
            }

            // 7) Devolver fila final
            var outRow = await db.QueryFirstOrDefaultAsync<Oib64Row>(@"
                SELECT id, orden_id, usuario_id, folio, mime_a, mime_b,
                       lado_a_b64, lado_b_b64, creado_en, actualizado_en
                FROM orden_imagenes_b64
                WHERE folio=@f LIMIT 1;", new { f = folio });

            return outRow;
        }

        // Copia suave a columnas nuevas de `ordenes` (si existen en ese ambiente)
        private static async Task TryCopyToOrdenesColumnsAsync(IDbConnection db, Oib64Row row)
        {
            try
            {
                const string sql = @"
UPDATE ordenes
   SET imagenA_mime = @ma,
       imagenA_b64  = @ba,
       imagenB_mime = @mb,
       imagenB_b64  = @bb,
       actualizado_en = NOW()
 WHERE id = @o;";
                await db.ExecuteAsync(sql, new
                {
                    o  = row.orden_id,
                    ma = row.mime_a ?? "image/jpeg",
                    mb = row.mime_b ?? "image/jpeg",
                    ba = (object?)row.lado_a_b64 ?? DBNull.Value,
                    bb = (object?)row.lado_b_b64 ?? DBNull.Value
                });
            }
            catch (MySqlException ex) when (ex.Number == 1054 || ex.Message.Contains("Unknown column", StringComparison.OrdinalIgnoreCase))
            {
                // Si no existen las columnas en ese entorno, se ignora sin romper.
            }
        }

        /* ==================== A / B (JSON o RAW) ==================== */
        private static async Task<IResult> SideAsync(HttpContext ctx, IConfiguration cfg, string folio, char side)
        {
            await using var db = OpenConn(cfg);

            var row = await db.QueryFirstOrDefaultAsync<Oib64Row?>(@"
                SELECT id, orden_id, usuario_id, folio, mime_a, mime_b,
                       lado_a_b64, lado_b_b64, creado_en, actualizado_en
                FROM orden_imagenes_b64
                WHERE folio=@f LIMIT 1;", new { f = folio });

            if (row is null)
            {
                var built = await BuildIfPossibleAsync(ctx, db, folio, force: false);
                if (built is null) return Results.NotFound();
                row = built;
            }

            var wantRaw = string.Equals(ctx.Request.Query["raw"], "1", StringComparison.OrdinalIgnoreCase);
            string? mime = (side == 'A') ? row.mime_a : row.mime_b;
            string? data = (side == 'A') ? row.lado_a_b64 : row.lado_b_b64;

            if (string.IsNullOrWhiteSpace(data)) return Results.NotFound();

            if (wantRaw)
            {
                // quitar "data:...;base64,"
                var comma = data.IndexOf(',', StringComparison.Ordinal);
                var b64 = comma >= 0 ? data[(comma + 1)..] : data;
                var bytes = Convert.FromBase64String(b64);
                return Results.File(bytes, mime ?? "image/jpeg");
            }

            return Results.Ok(new { folio, side = side.ToString(), mime, b64 = data });
        }
    }
}
