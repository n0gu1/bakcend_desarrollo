using System.Data;
using BaseUsuarios.Api.Models;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Repositories
{
    public sealed class UserRepository : IUserRepository
    {
        private readonly string _cs;
        public UserRepository(IConfiguration cfg)
        {
            _cs = cfg.GetConnectionString("Default")
                ?? throw new InvalidOperationException("Falta ConnectionStrings:Default");
        }

        public async Task<long> CreateAsync(UserCreateDto dto)
        {
            var phc = BCrypt.Net.BCrypt.EnhancedHashPassword(dto.Password, 12);

            byte[]? foto1 = ParseBase64(dto.PhotoBase64);
            byte[]? foto2 = ParseBase64(dto.Photo2Base64);
            var rolId = dto.RoleId ?? (byte)2;

            await using var conn = new MySqlConnection(_cs);
            await conn.OpenAsync();

            // Intento con teléfono
            var res = await ExecCrearAsync(conn, dto, phc, foto1, dto.PhotoMime, foto2, dto.Photo2Mime, rolId, dto.Phone);

            // Si SP bloquea por teléfono duplicado, reintenta sin teléfono (NULL)
            if (res.code == 409 && (res.message ?? "").Contains("Teléfono ya registrado", StringComparison.OrdinalIgnoreCase))
            {
                res = await ExecCrearAsync(conn, dto, phc, foto1, dto.PhotoMime, foto2, dto.Photo2Mime, rolId, null);
            }

            if (res.code != 0)
                throw new InvalidOperationException($"SP sp_usuarios_crear => Código {res.code}: {res.message}");

            return res.userId;
        }

        private static async Task<(int code, string? message, long userId)> ExecCrearAsync(
            MySqlConnection conn,
            UserCreateDto dto,
            string phc,
            byte[]? foto1, string? foto1Mime,
            byte[]? foto2, string? foto2Mime,
            byte rolId,
            string? phoneOverride)
        {
            await using var cmd = new MySqlCommand("sp_usuarios_crear", conn)
            { CommandType = CommandType.StoredProcedure };

            cmd.Parameters.Add(new MySqlParameter("p_Email", MySqlDbType.VarChar, 254) { Value = dto.Email });
            cmd.Parameters.Add(new MySqlParameter("p_Telefono", MySqlDbType.VarChar, 32) { Value = (object?)phoneOverride ?? DBNull.Value });
            cmd.Parameters.Add(new MySqlParameter("p_FechaNacimiento", MySqlDbType.Date) { Value = dto.Birthdate });
            cmd.Parameters.Add(new MySqlParameter("p_Nickname", MySqlDbType.VarChar, 50) { Value = dto.Nickname });
            cmd.Parameters.Add(new MySqlParameter("p_PasswordPHC", MySqlDbType.VarChar, 255) { Value = phc });

            var pFoto1 = new MySqlParameter("p_Fotografia", MySqlDbType.MediumBlob) { Value = (object?)foto1 ?? DBNull.Value };
            cmd.Parameters.Add(pFoto1);
            cmd.Parameters.Add(new MySqlParameter("p_FotografiaMime", MySqlDbType.VarChar, 64) { Value = (object?)foto1Mime ?? DBNull.Value });

            var pFoto2 = new MySqlParameter("p_Fotografia2", MySqlDbType.MediumBlob) { Value = (object?)foto2 ?? DBNull.Value };
            cmd.Parameters.Add(pFoto2);
            cmd.Parameters.Add(new MySqlParameter("p_Fotografia2Mime", MySqlDbType.VarChar, 64) { Value = (object?)foto2Mime ?? DBNull.Value });

            cmd.Parameters.Add(new MySqlParameter("p_RolId", MySqlDbType.UByte) { Value = rolId });

            var pUsuarioId = new MySqlParameter("p_UsuarioId", MySqlDbType.Int64) { Direction = ParameterDirection.Output };
            var pCodigo    = new MySqlParameter("p_Codigo", MySqlDbType.Int32)   { Direction = ParameterDirection.Output };
            var pMensaje   = new MySqlParameter("p_Mensaje", MySqlDbType.VarChar, 255) { Direction = ParameterDirection.Output };
            cmd.Parameters.Add(pUsuarioId);
            cmd.Parameters.Add(pCodigo);
            cmd.Parameters.Add(pMensaje);

            await cmd.ExecuteNonQueryAsync();

            var code    = Convert.ToInt32(pCodigo.Value ?? 0);
            var message = Convert.ToString(pMensaje.Value ?? "");
            var idVal   = pUsuarioId.Value;
            var userId  = idVal is null or DBNull ? 0L : Convert.ToInt64(idVal);
            return (code, message, userId);
        }

        private static byte[]? ParseBase64(string? dataUrl)
        {
            if (string.IsNullOrWhiteSpace(dataUrl)) return null;
            var raw = dataUrl.Contains(",") ? dataUrl.Split(',', 2)[1] : dataUrl;
            try { return Convert.FromBase64String(raw); } catch { return null; }
        }

        public async Task<UserDto?> GetByIdAsync(long id)
        {
            await using var conn = new MySqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_id", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_Id", id);

            await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
            if (!await rd.ReadAsync()) return null;

            return new UserDto
            {
                Id        = rd.GetInt64(rd.GetOrdinal("Id")),
                Email     = rd.GetString(rd.GetOrdinal("Email")),
                Phone     = rd.IsDBNull(rd.GetOrdinal("Telefono")) ? null : rd.GetString(rd.GetOrdinal("Telefono")),
                Birthdate = rd.IsDBNull(rd.GetOrdinal("FechaNacimiento")) ? null : rd.GetDateTime(rd.GetOrdinal("FechaNacimiento")),
                Nickname  = rd.GetString(rd.GetOrdinal("Nickname")),
                Activo    = rd.GetBoolean(rd.GetOrdinal("Activo")),
            };
        }

        public async Task<IReadOnlyList<UserDto>> ListAsync()
        {
            var items = new List<UserDto>();
            await using var conn = new MySqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("sp_usuarios_listar_todos", conn) { CommandType = CommandType.StoredProcedure };

            await using var rd = await cmd.ExecuteReaderAsync();
            while (await rd.ReadAsync())
            {
                items.Add(new UserDto
                {
                    Id        = rd.GetInt64(rd.GetOrdinal("Id")),
                    Email     = rd.GetString(rd.GetOrdinal("Email")),
                    Phone     = rd.IsDBNull(rd.GetOrdinal("Telefono")) ? null : rd.GetString(rd.GetOrdinal("Telefono")),
                    Birthdate = rd.IsDBNull(rd.GetOrdinal("FechaNacimiento")) ? null : rd.GetDateTime(rd.GetOrdinal("FechaNacimiento")),
                    Nickname  = rd.GetString(rd.GetOrdinal("Nickname")),
                    Activo    = rd.GetBoolean(rd.GetOrdinal("Activo")),
                });
            }
            return items;
        }

        public async Task<UserLoginResult> LoginAsync(string credential, string password)
        {
            var t = await GetPhcAndStatusAsync(credential);
            if (t is null) return new UserLoginResult { Success = false, Message = "Usuario no encontrado" };

            var (userId, phc, activo) = t.Value;
            if (!activo) return new UserLoginResult { Success = false, Message = "Usuario inactivo" };

            var ok = BCrypt.Net.BCrypt.EnhancedVerify(password, phc);
            return new UserLoginResult { Success = ok, UserId = ok ? userId : 0, Message = ok ? "OK" : "Credenciales inválidas" };
        }

        private async Task<(long id, string phc, bool activo)?> GetPhcAndStatusAsync(string credential)
        {
            await using var conn = new MySqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_Credencial", credential);

            await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
            if (!await rd.ReadAsync()) return null;

            var id     = rd.GetInt64(rd.GetOrdinal("Id"));
            var phc    = rd.GetString(rd.GetOrdinal("PasswordHash")); // cambia a "Phc" si tu SP así lo devuelve
            var activo = rd.GetBoolean(rd.GetOrdinal("Activo"));
            return (id, phc, activo);
        }

        public async Task<UserPhotoDto?> GetPhotoAsync(long id)
        {
            await using var conn = new MySqlConnection(_cs);
            await conn.OpenAsync();
            await using var cmd = new MySqlCommand("sp_usuarios_obtener_fotos", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_Id", id);

            await using var rd = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow);
            if (!await rd.ReadAsync()) return null;

            string? mime = rd.IsDBNull(rd.GetOrdinal("FotografiaMime")) ? null : rd.GetString(rd.GetOrdinal("FotografiaMime"));

            string? b64 = null;
            if (!rd.IsDBNull(rd.GetOrdinal("Fotografia")))
            {
                var len = rd.GetBytes(rd.GetOrdinal("Fotografia"), 0, null, 0, 0);
                var buf = new byte[len];
                rd.GetBytes(rd.GetOrdinal("Fotografia"), 0, buf, 0, (int)len);
                b64 = Convert.ToBase64String(buf);
                if (!string.IsNullOrWhiteSpace(mime))
                    b64 = $"data:{mime};base64,{b64}";
            }
            return new UserPhotoDto { UserId = id, Mime = mime, PhotoBase64 = b64 };
        }

        public async Task<bool> UpdatePhotoAsync(UserPhotoDto dto)
        {
            byte[]? bytes = ParseBase64(dto.PhotoBase64);
            await using var conn = new MySqlConnection(_cs);
            await conn.OpenAsync();

            await using var cmd = new MySqlCommand("sp_usuarios_actualizar_foto", conn) { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_Id", dto.UserId);

            var pFoto = new MySqlParameter("p_Fotografia", MySqlDbType.MediumBlob) { Value = (object?)bytes ?? DBNull.Value };
            cmd.Parameters.Add(pFoto);
            cmd.Parameters.AddWithValue("p_FotografiaMime", (object?)dto.Mime ?? DBNull.Value);

            var n = await cmd.ExecuteNonQueryAsync();
            return n > 0;
        }
    }
}