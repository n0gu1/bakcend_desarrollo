// BaseUsuarios.Api/src/Endpoints/UserPhotoEndpoints.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using System;
using System.Data;
using System.Threading.Tasks;

namespace BaseUsuarios.Api.Endpoints
{
    /// <summary>
    /// Endpoints para obtener la foto del usuario (BLOB) y su nickname desde la BD "Default".
    /// Usa tu SP: sp_usuarios_obtener_fotos(IN p_UsuarioId, OUT p_Codigo, OUT p_Mensaje)
    /// que devuelve un resultset con columnas: UsuarioId, Fotografia, Fotografia2.
    /// </summary>
    public static class UserPhotoEndpoints
    {
        public static IEndpointRouteBuilder MapUserPhotoEndpoints(this IEndpointRouteBuilder app)
        {
            // 1) Foto en DataURL (JSON)
            app.MapGet("/api/users/{userId:long}/photo-dataurl", async (long userId, IConfiguration cfg) =>
            {
                var photo = await GetPhotoBytesAsync(cfg, userId);
                if (photo == null || photo.Length == 0)
                    return Results.NotFound(new { message = "Sin foto" });

                var mime = DetectMime(photo) ?? "image/jpeg";
                return Results.Ok(new { dataUrl = $"data:{mime};base64,{Convert.ToBase64String(photo)}" });
            })
            .WithTags("Users")
            .WithName("UserPhotoDataUrl");

            // 2) Foto binaria directa
            app.MapGet("/api/users/{userId:long}/photo", async (long userId, IConfiguration cfg) =>
            {
                var photo = await GetPhotoBytesAsync(cfg, userId);
                if (photo == null || photo.Length == 0)
                    return Results.NotFound();

                var mime = DetectMime(photo) ?? "image/jpeg";
                return Results.File(photo, mime);
            })
            .WithTags("Users")
            .WithName("UserPhotoBinary");

            // 3) Perfil combinado: nickname + foto (DataURL)
            app.MapGet("/api/users/{userId:long}/profile", async (long userId, IConfiguration cfg) =>
            {
                var cs = cfg.GetConnectionString("Default");
                if (string.IsNullOrWhiteSpace(cs))
                    return Results.Problem("Cadena de conexión 'Default' no configurada.", statusCode: 500);

                string? nickname = null;
                byte[]? photo = null;

                await using (var conn = new MySqlConnection(cs))
                {
                    await conn.OpenAsync();

                    // Nickname
                    await using (var cmdNick = new MySqlCommand(
                        "SELECT Nickname FROM usuarios WHERE UsuarioId=@u LIMIT 1;", conn))
                    {
                        cmdNick.Parameters.AddWithValue("@u", userId);
                        var o = await cmdNick.ExecuteScalarAsync();
                        nickname = o == null ? null : Convert.ToString(o);
                    }

                    // Foto (prioriza Fotografia2 si existe)
                    photo = await GetPhotoBytesAsync(cfg, userId, conn);
                }

                string? dataUrl = null;
                if (photo != null && photo.Length > 0)
                {
                    var mime = DetectMime(photo) ?? "image/jpeg";
                    dataUrl = $"data:{mime};base64,{Convert.ToBase64String(photo)}";
                }

                return Results.Ok(new { userId, nickname, dataUrl });
            })
            .WithTags("Users")
            .WithName("UserProfile");

            // 4) Sólo nickname (útil como fallback)
            app.MapGet("/api/users/{userId:long}/nickname", async (long userId, IConfiguration cfg) =>
            {
                var cs = cfg.GetConnectionString("Default");
                if (string.IsNullOrWhiteSpace(cs))
                    return Results.Problem("Cadena 'Default' no configurada.", statusCode: 500);

                string? nickname = null;
                await using (var conn = new MySqlConnection(cs))
                {
                    await conn.OpenAsync();
                    await using var cmd = new MySqlCommand(
                        "SELECT Nickname FROM usuarios WHERE UsuarioId=@u LIMIT 1;", conn);
                    cmd.Parameters.AddWithValue("@u", userId);
                    var o = await cmd.ExecuteScalarAsync();
                    nickname = o == null ? null : Convert.ToString(o);
                }

                if (string.IsNullOrWhiteSpace(nickname))
                    return Results.NotFound(new { message = "Sin nickname" });

                return Results.Ok(new { userId, nickname });
            })
            .WithTags("Users")
            .WithName("UserNickname");

            return app;
        }

        /// <summary>
        /// Llama tu SP de fotos y devuelve Fotografia2 si existe, si no Fotografia.
        /// </summary>
        private static async Task<byte[]?> GetPhotoBytesAsync(IConfiguration cfg, long userId, MySqlConnection? externalConn = null)
        {
            var cs = cfg.GetConnectionString("Default");
            if (string.IsNullOrWhiteSpace(cs)) return null;

            var ownConn = externalConn == null;
            MySqlConnection conn = externalConn ?? new MySqlConnection(cs);
            if (ownConn) await conn.OpenAsync();

            try
            {
                byte[]? foto1 = null; // Fotografia (login)
                byte[]? foto2 = null; // Fotografia2 (personalizada)

                await using (var cmd = new MySqlCommand("CALL sp_usuarios_obtener_fotos(@u, @cod, @msg);", conn))
                {
                    cmd.Parameters.AddWithValue("@u", userId);
                    cmd.Parameters.Add(new MySqlParameter("@cod", MySqlDbType.Int32) { Direction = ParameterDirection.Output });
                    cmd.Parameters.Add(new MySqlParameter("@msg", MySqlDbType.VarChar, 255) { Direction = ParameterDirection.Output });

                    await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                    if (await rd.ReadAsync())
                    {
                        int idxFoto  = SafeOrdinal(rd, "Fotografia");
                        int idxFoto2 = SafeOrdinal(rd, "Fotografia2");

                        if (idxFoto  >= 0 && !rd.IsDBNull(idxFoto))   foto1 = ReadBlobOneShot(rd, idxFoto);
                        if (idxFoto2 >= 0 && !rd.IsDBNull(idxFoto2))  foto2 = ReadBlobOneShot(rd, idxFoto2);
                    }
                }

                return (foto2 != null && foto2.Length > 0) ? foto2 : foto1;
            }
            finally
            {
                if (ownConn) await conn.DisposeAsync();
            }
        }

        /// <summary>
        /// Lee un BLOB en una sola llamada (evita error “Data index must be a valid index in the field” del driver).
        /// </summary>
        private static byte[] ReadBlobOneShot(MySqlDataReader rd, int ordinal)
        {
            long len = rd.GetBytes(ordinal, 0, null!, 0, 0); // longitud total
            if (len <= 0) return Array.Empty<byte>();

            var buffer = new byte[len];
            long read = rd.GetBytes(ordinal, 0, buffer, 0, (int)len); // lectura única
            if (read < len)
            {
                var tmp = new byte[read];
                Array.Copy(buffer, tmp, read);
                return tmp;
            }
            return buffer;
        }

        /// <summary>Obtiene el ordinal de la columna de forma segura (case-insensitive). Devuelve -1 si no existe.</summary>
        private static int SafeOrdinal(MySqlDataReader rd, string name)
        {
            for (int i = 0; i < rd.FieldCount; i++)
                if (string.Equals(rd.GetName(i), name, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        /// <summary>Detección simple del MIME por "magic numbers".</summary>
        private static string? DetectMime(byte[] data)
        {
            if (data.Length >= 12)
            {
                // JPEG
                if (data[0] == 0xFF && data[1] == 0xD8) return "image/jpeg";
                // PNG
                if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return "image/png";
                // GIF
                if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return "image/gif";
                // WEBP (RIFF....WEBP)
                if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46 &&
                    data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return "image/webp";
            }
            return null;
        }
    }
}
