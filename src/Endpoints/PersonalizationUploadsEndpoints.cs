using System.Globalization;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints
{
    public static class PersonalizationUploadsEndpoints
    {
        public static IEndpointRouteBuilder MapPersonalizationUploadsEndpoints(this IEndpointRouteBuilder app)
        {
            // POST /api/local/uploads (principal)
            app.MapPost("/api/local/uploads", Upload)
               .Accepts<IFormFile>("multipart/form-data")
               .Produces(StatusCodes.Status200OK)
               .WithName("PersonalizationUpload");

            // Alias opcional por si el front hace fallback
            app.MapPost("/api/uploads", Upload);

            return app;
        }

        private static async Task<IResult> Upload(
            HttpContext http,
            MySqlConnection cnn)
        {
            // ── 1) Validar multipart/form-data
            if (!http.Request.HasFormContentType)
                return Results.BadRequest(new { message = "content-type inválido" });

            var form = await http.Request.ReadFormAsync();
            var file = form.Files.GetFile("file");
            if (file == null || file.Length == 0)
                return Results.BadRequest(new { message = "archivo requerido" });

            // personalizationId es opcional para este endpoint; lo usa el front al crear la capa
            // (no lo necesitamos aquí para guardar el archivo)
            // var personalizationId = form["personalizationId"].ToString();

            // ── 2) Preparar wwwroot/uploads
            var env = http.RequestServices.GetRequiredService<IWebHostEnvironment>();
            var webRoot = env.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
            var uploadsDir = Path.Combine(webRoot, "uploads");
            Directory.CreateDirectory(uploadsDir);

            // ── 3) Guardar archivo físico
            var ext = Path.GetExtension(file.FileName);
            var name = $"{Guid.NewGuid():N}{ext}";
            var physicalPath = Path.Combine(uploadsDir, name);
            await using (var fs = new FileStream(physicalPath, FileMode.CreateNew))
            {
                await file.CopyToAsync(fs);
            }

            // Mime (best effort)
            var contentType = file.ContentType;
            if (string.IsNullOrWhiteSpace(contentType))
            {
                contentType = ext.ToLowerInvariant() switch
                {
                    ".png"  => "image/png",
                    ".jpg"  => "image/jpeg",
                    ".jpeg" => "image/jpeg",
                    ".gif"  => "image/gif",
                    _       => "application/octet-stream"
                };
            }

            // Para ancho/alto intentamos abrir como imagen; si falla, seguimos
            int? width = null, height = null;
            try
            {
                using var img = await SixLabors.ImageSharp.Image.LoadAsync(physicalPath);
                width  = img.Width;
                height = img.Height;
            }
            catch { /* no pasa nada */ }

            var relPath = $"uploads/{name}";        // relativo a wwwroot
            var now     = DateTime.Now;

            // ── 4) Insertar en compras.archivos
            const string sql = @"
INSERT INTO compras.archivos
(tipo, ruta, mime, ancho, alto, bytes, propietario_tipo, propietario_id, creado_en)
VALUES (@tipo, @ruta, @mime, @ancho, @alto, @bytes, @propietario_tipo, @propietario_id, @creado_en);
SELECT LAST_INSERT_ID();";

            var archivoId = await cnn.ExecuteScalarAsync<long>(sql, new
            {
                tipo = "foto",
                ruta = relPath,
                mime = contentType,
                ancho = width,
                alto = height,
                bytes = (int?)file.Length,
                propietario_tipo = "personalizacion",
                propietario_id = (long?)null,
                creado_en = now
            });

            // ── 5) Devolver JSON con id y URL estática servida por UseStaticFiles
            var publicUrl = $"/{relPath}".Replace("\\", "/");

            return Results.Ok(new
            {
                archivoId,
                url = publicUrl
            });
        }
    }
}
