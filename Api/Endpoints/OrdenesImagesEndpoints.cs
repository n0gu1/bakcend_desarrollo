// BaseUsuarios.Api/Endpoints/OrdenesImagesEndpoints.cs
using System;
using System.Data;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    /// <summary>
    /// Endpoints para subir una imagen y guardarla DIRECTO en la tabla `ordenes`
    /// (imagenA_mime/imagenA_b64 o imagenB_mime/imagenB_b64). No se escribe en
    /// ninguna otra tabla ni se guarda archivo en disco (solo base64 en BD).
    /// </summary>
    public static class OrdenesImagesEndpoints
    {
        public static IEndpointRouteBuilder MapOrdenesImagesEndpoints(this IEndpointRouteBuilder app)
        {
            // Forma REST clara: lado en la ruta
            var gLocal = app.MapGroup("/api/local").WithTags("OrdenesImages");
            gLocal.MapPost("/orders/{folio}/images/{side}", UploadToOrdenesByRouteAsync);

            // Alias sin /local por compatibilidad
            var g = app.MapGroup("/api").WithTags("OrdenesImages");
            g.MapPost("/orders/{folio}/images/{side}", UploadToOrdenesByRouteAsync);

            // Compatibilidad con el front que ya llama /api/local/uploads enviando folio & lado en el form
            // Si vienen folio+lado -> actualiza ordenes; si no, 400.
            gLocal.MapPost("/uploads", UploadToOrdenesFromFormAsync);
            g.MapPost("/uploads", UploadToOrdenesFromFormAsync);

            return app;
        }

        /* ================ ConnString (ComprasLocal) ================ */
        // Según indicaste: la conexión correcta es ConnectionStrings:ComprasLocal
        private static MySqlConnection OpenConn(IConfiguration cfg)
        {
            var cs = cfg.GetConnectionString("ComprasLocal")
                     ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");
            var c = new MySqlConnection(cs);
            c.Open();
            return c;
        }

        /* ===================== Helpers ===================== */
        private static string NormalizeSide(string s)
        {
            s = (s ?? "").Trim().ToUpperInvariant();
            return (s == "A" || s == "B") ? s : "";
        }

        private static string DetectMime(string? contentType, string? fileName)
        {
            var ct = (contentType ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ct)) return ct;

            var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
            return ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png"            => "image/png",
                ".gif"            => "image/gif",
                ".webp"           => "image/webp",
                _                 => "image/jpeg"
            };
        }

        private static async Task<(string b64, string mime)> ReadAsBase64Async(IFormFile file)
        {
            await using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            var b64 = Convert.ToBase64String(bytes);
            var mime = DetectMime(file.ContentType, file.FileName);
            return (b64, mime);
        }

        private static async Task<int> UpdateOrdenImagenAsync(IDbConnection db, IDbTransaction tx,
            string folio, string side, string mime, string b64)
        {
            const string sqlA = @"
UPDATE ordenes
   SET imagenA_mime = @m,
       imagenA_b64  = @b,
       actualizado_en = NOW()
 WHERE folio = @f
 LIMIT 1;";

            const string sqlB = @"
UPDATE ordenes
   SET imagenB_mime = @m,
       imagenB_b64  = @b,
       actualizado_en = NOW()
 WHERE folio = @f
 LIMIT 1;";

            var sql = (side == "A") ? sqlA : sqlB;
            return await db.ExecuteAsync(new CommandDefinition(sql, new { f = folio, m = mime, b = b64 }, transaction: tx));
        }

        private static async Task<bool> OrdenExisteAsync(IDbConnection db, IDbTransaction? tx, string folio)
        {
            const string sql = "SELECT COUNT(*) FROM ordenes WHERE folio=@f LIMIT 1;";
            var n = await db.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { f = folio }, transaction: tx));
            return n > 0;
        }

        /* =============== Endpoint: ruta /orders/{folio}/images/{side} =============== */
        private static async Task<IResult> UploadToOrdenesByRouteAsync(
            HttpContext ctx, IConfiguration cfg, string folio, string side)
        {
            var form = await ctx.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { message = "file requerido (multipart/form-data)" });

            side = NormalizeSide(side);
            if (string.IsNullOrEmpty(side))
                return Results.BadRequest(new { message = "side debe ser 'A' o 'B'" });

            folio = (folio ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folio))
                return Results.BadRequest(new { message = "folio requerido en la ruta" });

            var (b64, mime) = await ReadAsBase64Async(file);

            await using var db = OpenConn(cfg);
            await using var tx = await db.BeginTransactionAsync();

            // Verifica que exista la orden
            if (!await OrdenExisteAsync(db, tx, folio))
            {
                await tx.RollbackAsync();
                return Results.NotFound(new { message = "Orden no encontrada por folio" });
            }

            var rows = await UpdateOrdenImagenAsync(db, tx, folio, side, mime, b64);
            await tx.CommitAsync();

            if (rows == 0)
                return Results.NotFound(new { message = "No se actualizó ninguna fila" });

            return Results.Ok(new
            {
                ok = true,
                folio,
                side,
                mime,
                length = b64.Length
            });
        }

        /* =============== Endpoint: compat /uploads (leyendo folio & lado del form) =============== */
        private static async Task<IResult> UploadToOrdenesFromFormAsync(HttpContext ctx, IConfiguration cfg)
        {
            var form = await ctx.Request.ReadFormAsync();

            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { message = "file requerido (multipart/form-data)" });

            var folio = (form["folio"].ToString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(folio))
                return Results.BadRequest(new { message = "folio requerido en el form" });

            var side = NormalizeSide(form["lado"]);
            if (string.IsNullOrEmpty(side))
                return Results.BadRequest(new { message = "lado debe ser 'A' o 'B' en el form" });

            var (b64, mime) = await ReadAsBase64Async(file);

            await using var db = OpenConn(cfg);
            await using var tx = await db.BeginTransactionAsync();

            if (!await OrdenExisteAsync(db, tx, folio))
            {
                await tx.RollbackAsync();
                return Results.NotFound(new { message = "Orden no encontrada por folio" });
            }

            var rows = await UpdateOrdenImagenAsync(db, tx, folio, side, mime, b64);
            await tx.CommitAsync();

            return Results.Ok(new
            {
                ok = rows > 0,
                folio,
                side,
                mime,
                length = b64.Length
            });
        }
    }
}
