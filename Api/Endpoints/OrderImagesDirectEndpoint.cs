// BaseUsuarios.Api/Endpoints/OrderImagesDirectEndpoint.cs
using System;
using System.IO;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class OrderImagesDirectEndpoint
    {
        public static IEndpointRouteBuilder MapOrderImagesDirectEndpoints(this IEndpointRouteBuilder app)
        {
            var g = app.MapGroup("/api").WithTags("OrderImagesDirect");

            // Con scope explícito
            g.MapPost("/{scope:regex((?i)^(local|default)$)}/orders/images", UploadAsync);

            // Compatibilidad sin scope
            g.MapPost("/local/orders/images", (HttpContext ctx, IConfiguration cfg) => UploadAsync(ctx, cfg, "local"));
            g.MapPost("/orders/images", (HttpContext ctx, IConfiguration cfg) => UploadAsync(ctx, cfg, "default"));

            return app;
        }

        /* ================== Upload ================== */
        private static async Task<IResult> UploadAsync(HttpContext ctx, IConfiguration cfg, string scope)
        {
            // --- 1) Leer multipart ---
            if (!ctx.Request.HasFormContentType)
                return Results.BadRequest(new { message = "Content-Type debe ser multipart/form-data" });

            var form = await ctx.Request.ReadFormAsync();

            // usuarioId (obligatorio)
            if (!long.TryParse(form["usuarioId"], out var usuarioId) || usuarioId <= 0)
                return Results.BadRequest(new { message = "usuarioId inválido o faltante" });

            // draftItemId (muy recomendado: asegura la MISMA orden)
            long draftItemId = 0;
            // intentamos por form, query o header (robusto)
            if (!long.TryParse(form["draftItemId"], out draftItemId) || draftItemId <= 0)
            {
                long.TryParse(ctx.Request.Query["draftItemId"], out draftItemId);
                if (draftItemId <= 0)
                {
                    long.TryParse(ctx.Request.Headers["X-Draft-Item-Id"], out draftItemId);
                }
            }
            if (draftItemId <= 0)
            {
                // Último fallback: usar un DRAFT abierto del usuario (si existe).
                // OJO: lo ideal es que el front SIEMPRE mande draftItemId.
                draftItemId = 0;
            }

            // side (A/B)
            var sideRaw = (string?)form["side"] ?? "A";
            var side = char.ToUpperInvariant(sideRaw.Length > 0 ? sideRaw[0] : 'A');
            if (side != 'A' && side != 'B') side = 'A';

            // archivo
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { message = "file faltante o vacío" });

            var mime = !string.IsNullOrWhiteSpace(file.ContentType) ? file.ContentType : "image/jpeg";

            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                await file.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var b64 = Convert.ToBase64String(bytes);
            var dataUrl = $"data:{mime};base64,{b64}";
            var len = dataUrl.Length;

            await using var db = OpenConn(cfg, scope);
            await db.OpenAsync();
            await using var tx = await db.BeginTransactionAsync();

            try
            {
                // --- 2) Asegurar UNA sola orden DRAFT para (usuarioId, draftItemId) ---
                var orderId = await EnsureDraftOrderAsync(db, (MySqlTransaction)tx, usuarioId, draftItemId);

                // --- 3) Escribir la imagen en columnas de la orden ---
                string sqlUpdate = side == 'A'
                    ? @"UPDATE ordenes
                          SET imagenA_mime = @m,
                              imagenA_b64  = @d,
                              actualizado_en = NOW()
                        WHERE id = @id;"
                    : @"UPDATE ordenes
                          SET imagenB_mime = @m,
                              imagenB_b64  = @d,
                              actualizado_en = NOW()
                        WHERE id = @id;";

                await db.ExecuteAsync(sqlUpdate, new { id = orderId, m = mime, d = dataUrl }, tx);

                // Obtener folio para responder
                var folio = await db.ExecuteScalarAsync<string>(
                    "SELECT folio FROM ordenes WHERE id=@id LIMIT 1;", new { id = orderId }, tx);

                await tx.CommitAsync();

                return Results.Ok(new
                {
                    ok = true,
                    orderId,
                    folio,
                    side = side.ToString(),
                    mime,
                    len
                });
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                return Results.Problem(ex.Message);
            }
        }

        /* ================== Helpers ================== */

        private static MySqlConnection OpenConn(IConfiguration cfg, string scope)
        {
            var cs = scope.Equals("local", StringComparison.OrdinalIgnoreCase)
                ? cfg.GetConnectionString("ComprasLocal")
                : cfg.GetConnectionString("Compras");

            if (string.IsNullOrWhiteSpace(cs))
            {
                var name = scope.Equals("local", StringComparison.OrdinalIgnoreCase) ? "ComprasLocal" : "Compras";
                throw new InvalidOperationException($"Falta connectionString '{name}' en la configuración.");
            }

            return new MySqlConnection(cs);
        }

        /// <summary>
        /// Asegura UNA orden DRAFT por (usuarioId, draftItemId).
        /// Estrategia:
        /// 1) Si draftItemId > 0: busca por (usuario_id, draft_item_id).
        /// 2) Si no existe, busca por folio 'DRAFT-uid-draft' (legacy) y si la encuentra, le rellena draft_item_id.
        /// 3) Si todavía no existe, inserta una nueva con ese folio y draft_item_id.
        /// 4) Si draftItemId == 0, intenta reutilizar la última DRAFT del usuario; si no, crea una con folio 'DRAFT-uid-0'.
        /// </summary>
        private static async Task<long> EnsureDraftOrderAsync(
            MySqlConnection db, MySqlTransaction tx, long usuarioId, long draftItemId)
        {
            // Caso normal (ideal): draftItemId > 0
            if (draftItemId > 0)
            {
                // 1) Buscar por (usuario, draft_item_id)
                var byPair = await db.ExecuteScalarAsync<long?>(
                    @"SELECT id FROM ordenes WHERE usuario_id=@u AND draft_item_id=@d LIMIT 1;",
                    new { u = usuarioId, d = draftItemId }, tx);
                if (byPair.HasValue) return byPair.Value;

                // 2) Buscar por folio legacy y reparar draft_item_id si está NULL
                var folio = $"DRAFT-{usuarioId}-{draftItemId}";
                var row = await db.QueryFirstOrDefaultAsync<(long id, long? draft_item_id)>(
                    @"SELECT id, draft_item_id FROM ordenes WHERE folio=@f LIMIT 1;",
                    new { f = folio }, tx);

                if (row.id != 0)
                {
                    if (!row.draft_item_id.HasValue)
                    {
                        await db.ExecuteAsync(
                            @"UPDATE ordenes SET draft_item_id=@d, actualizado_en=NOW() WHERE id=@id;",
                            new { d = draftItemId, id = row.id }, tx);
                    }
                    return row.id;
                }

                // 3) No existe: crear nueva DRAFT
                var newId = await db.ExecuteScalarAsync<long>(
                    @"INSERT INTO ordenes (usuario_id, folio, estado, total, draft_item_id, creado_en, actualizado_en)
                      VALUES (@u, @f, 'DRAFT', 0, @d, NOW(), NOW());
                      SELECT LAST_INSERT_ID();",
                    new { u = usuarioId, f = folio, d = draftItemId }, tx);
                return newId;
            }

            // Fallback (draftItemId == 0): intentar reutilizar última DRAFT del usuario
            var lastDraft = await db.ExecuteScalarAsync<long?>(
                @"SELECT id FROM ordenes
                  WHERE usuario_id=@u AND estado='DRAFT'
                  ORDER BY id DESC LIMIT 1;",
                new { u = usuarioId }, tx);
            if (lastDraft.HasValue) return lastDraft.Value;

            // Crear DRAFT con folio DRAFT-uid-0
            var folio0 = $"DRAFT-{usuarioId}-0";
            var newId0 = await db.ExecuteScalarAsync<long>(
                @"INSERT INTO ordenes (usuario_id, folio, estado, total, draft_item_id, creado_en, actualizado_en)
                  VALUES (@u, @f, 'DRAFT', 0, NULL, NOW(), NOW());
                  SELECT LAST_INSERT_ID();",
                new { u = usuarioId, f = folio0 }, tx);
            return newId0;
        }
    }
}
