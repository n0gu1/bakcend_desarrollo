// BaseUsuarios.Api/Endpoints/ShopImagesDirectEndpoints.cs
using System;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    /// <summary>
    /// Sube imagen A/B y la inserta/sustituye inmediatamente en columnas imagenA_*/imagenB_* de la tabla `ordenes`.
    /// Si el usuario no tiene una orden en estado CRE, crea una orden mínima (proceso ORD, estado CRE).
    /// </summary>
    public static class ShopImagesDirectEndpoints
    {
        public static IEndpointRouteBuilder MapShopImagesDirectEndpoints(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/api/local/orders").WithTags("ShopImagesDirect");

            // POST /api/local/orders/images   (multipart/form-data)
            // form: usuarioId(number), side("A"|"B"), file(IFormFile)
            g.MapPost("/images", UploadImageAsync);

            return app;
        }

        /* ================= Helpers ================= */

        private static string GetConnComprasLocal(IConfiguration cfg) =>
            cfg.GetConnectionString("ComprasLocal")
             ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");

        private static async Task<long> EnsureDraftOrderAsync(MySqlConnection db, IDbTransaction tx, long usuarioId)
        {
            // 1) ¿Hay una orden CRE para este usuario?
            const string sqlFind = @"
SELECT o.id
FROM ordenes o
JOIN procesos p ON p.id = o.proceso_id AND p.codigo='ORD'
JOIN estados  e ON e.id = o.estado_actual_id AND e.proceso_id=p.id AND e.codigo='CRE'
WHERE o.usuario_id = @uid
ORDER BY o.creado_en DESC
LIMIT 1;";
            var existing = await db.ExecuteScalarAsync<long?>(
                new CommandDefinition(sqlFind, new { uid = usuarioId }, transaction: tx));
            if (existing is long okId) return okId;

            // 2) IDs proceso/estado
            const string sqlProc = "SELECT id FROM procesos WHERE codigo='ORD' LIMIT 1;";
            var pid = await db.ExecuteScalarAsync<long>(
                new CommandDefinition(sqlProc, transaction: tx));

            const string sqlEst = "SELECT id FROM estados WHERE proceso_id=@pid AND codigo='CRE' LIMIT 1;";
            var estId = await db.ExecuteScalarAsync<long>(
                new CommandDefinition(sqlEst, new { pid }, transaction: tx));

            // 3) Folio único: yyyymmdd-#### (reintenta si colisiona)
            string folio;
            int tries = 0;
            do
            {
                var rnd = Random.Shared.Next(1000, 9999);
                folio = $"{DateTime.UtcNow:yyyyMMdd}-{rnd}";
                const string sqlChk = "SELECT 1 FROM ordenes WHERE folio=@f LIMIT 1;";
                var exists = await db.ExecuteScalarAsync<int?>(
                    new CommandDefinition(sqlChk, new { f = folio }, transaction: tx));
                if (exists is null) break;
            } while (++tries < 5);

            // 4) Crear orden mínima
            const string sqlIns = @"
INSERT INTO ordenes
  (usuario_id, folio, total, proceso_id, estado_actual_id, metodo_pago, estado_pago, creado_en, actualizado_en)
VALUES
  (@uid, @folio, 0, @pid, @est, 'efectivo', 'pendiente', NOW(), NOW());
SELECT LAST_INSERT_ID();";
            var orderId = await db.ExecuteScalarAsync<long>(
                new CommandDefinition(sqlIns, new { uid = usuarioId, folio, pid, est = estId }, transaction: tx));

            return orderId;
        }

        private static (string Mime, string DataUrl) ToDataUrl(IFormFile file)
        {
            var mime = !string.IsNullOrWhiteSpace(file.ContentType) ? file.ContentType : "image/jpeg";
            using var ms = new MemoryStream();
            file.CopyTo(ms);
            var bytes = ms.ToArray();
            var b64 = Convert.ToBase64String(bytes);
            var dataUrl = $"data:{mime};base64,{b64}";
            return (mime, dataUrl);
        }

        /* ================ Endpoint ================ */

        private sealed record UploadResp(bool ok, long orderId, string folio, string side, string mime, int len);

        private static async Task<IResult> UploadImageAsync(
            [FromServices] IConfiguration cfg,
            HttpRequest req)
        {
            // Validación de form-data
            if (!req.HasFormContentType)
                return Results.BadRequest(new { message = "Se requiere multipart/form-data" });

            var form = await req.ReadFormAsync();
            if (!long.TryParse(form["usuarioId"], out var usuarioId) || usuarioId <= 0)
                return Results.BadRequest(new { message = "usuarioId requerido" });

            var side = (form["side"].ToString() ?? "").Trim().ToUpperInvariant();
            if (side != "A" && side != "B")
                return Results.BadRequest(new { message = "side debe ser 'A' o 'B'" });

            var file = form.Files["file"];
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { message = "file requerido" });

            await using var db = new MySqlConnection(GetConnComprasLocal(cfg));
            await db.OpenAsync();
            await using var tx = await db.BeginTransactionAsync();

            try
            {
                // Garantiza (o crea) orden CRE del usuario
                var orderId = await EnsureDraftOrderAsync(db, tx, usuarioId);

                // Obtén folio actual de la orden
                const string sqlFolio = "SELECT folio FROM ordenes WHERE id=@id LIMIT 1;";
                var folio = await db.ExecuteScalarAsync<string?>(
                    new CommandDefinition(sqlFolio, new { id = orderId }, transaction: tx)) ?? "";

                // Convierte a dataURL
                var (mime, dataUrl) = ToDataUrl(file);
                var len = dataUrl.Length;

                // Update columna A o B (si las columnas no existen -> 1054)
                string sqlUpd = side == "A"
                    ? @"UPDATE ordenes SET imagenA_mime=@m, imagenA_b64=@d, actualizado_en=NOW() WHERE id=@id;"
                    : @"UPDATE ordenes SET imagenB_mime=@m, imagenB_b64=@d, actualizado_en=NOW() WHERE id=@id;";
                try
                {
                    await db.ExecuteAsync(new CommandDefinition(sqlUpd, new { id = orderId, m = mime, d = dataUrl }, transaction: tx));
                }
                catch (MySqlException ex) when (ex.Number == 1054 || ex.Message.Contains("Unknown column", StringComparison.OrdinalIgnoreCase))
                {
                    // Si las columnas no existen, abortamos con mensaje claro.
                    return Results.BadRequest(new
                    {
                        message = "Las columnas imagenA_mime/imagenA_b64/imagenB_mime/imagenB_b64 no existen en `ordenes`. Ejecuta el ALTER TABLE primero."
                    });
                }

                await tx.CommitAsync();
                return Results.Ok(new UploadResp(true, orderId, folio, side, mime, len));
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }
    }
}
