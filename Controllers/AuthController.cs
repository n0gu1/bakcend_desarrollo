// BaseUsuarios.Api/Controllers/AuthController.cs
using System.Data;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;
using System.Collections.Concurrent;
using System.Globalization;

namespace BaseUsuarios.Api.Controllers;

[ApiController]
[Route("api/[controller]")] // api/auth
public class AuthController : ControllerBase
{
    private readonly IConfiguration _cfg;
    private readonly ILogger<AuthController> _log;
    private readonly IHttpClientFactory _httpFactory;

    public AuthController(IConfiguration cfg, ILogger<AuthController> log, IHttpClientFactory httpFactory)
    {
        _cfg = cfg; _log = log; _httpFactory = httpFactory;
    }

    private string Cs => _cfg.GetConnectionString("Default")!;

    // ===================== DTOs =====================
    public class LoginDto
    {
        public string? Credential { get; set; }
        public string? Password { get; set; }
    }

    public class LoginFacialFastRequest
    {
        public string? PhotoBase64 { get; set; }
        public int MinPercent { get; set; } = 60;
        public ulong? UsuarioId { get; set; } // opcional
        public ulong? UserId { get; set; }    // alias opcional
        public string? Credential { get; set; }
    }

    public class LoginFacialDto
    {
        public string? PhotoBase64 { get; set; }
        public int MaxCandidates { get; set; } = 75;
        public int DegreeOfParallelism { get; set; } = 12;
        public int MinPercent { get; set; } = 60;
    }

    // ===== Registro =====
    public class RegisterDto
    {
        public string? Email { get; set; }
        public string? Phone { get; set; }
        public string? Birthdate { get; set; } // "YYYY-MM-DD"
        public string? Nickname { get; set; }
        public string? Password { get; set; }
        public string? PhotoBase64 { get; set; }   // original (login)
        public string? PhotoMime { get; set; }
        public string? Photo2Base64 { get; set; }  // editada (filtros/stickers)
        public string? Photo2Mime { get; set; }
        public byte? RoleId { get; set; } = 2;
    }

    public record FaceVerifyRequest(string RostroA, string RostroB);
    public record FaceVerifyResponse(bool resultado, bool coincide, string? status, string? score);

    // ===================== Helpers =====================
    private static string StripDataUrlOrReturn(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var m = Regex.Match(s, @"^data:(?<mime>[^;]+);base64,(?<data>.+)$");
        return m.Success ? m.Groups["data"].Value : s.Trim();
    }

    private static string BlobToBase64OrUseAsIs(byte[] blob)
    {
        if (blob == null || blob.Length == 0) return "";
        // Si el blob en realidad era texto base64, respétalo
        var text = Encoding.UTF8.GetString(blob).Trim();
        var m = Regex.Match(text, @"^data:(?<mime>[^;]+);base64,(?<data>.+)$");
        if (m.Success) text = m.Groups["data"].Value;
        text = Regex.Replace(text, @"\s+", "");
        if (Regex.IsMatch(text, @"^[A-Za-z0-9+/=]+$") && (text.Length % 4 == 0))
        {
            try
            {
                var dec = Convert.FromBase64String(text);
                if (dec.Length > 0) return text; // ya era base64 válido
            }
            catch { /* no era base64 */ }
        }
        // binario → a base64
        return Convert.ToBase64String(blob);
    }

    private static byte[]? DecodeDataUrlToBytes(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        var s = dataUrl.Trim();
        var m = Regex.Match(s, @"^data:(?<mime>[^;]+);base64,(?<data>.+)$");
        var b64 = m.Success ? m.Groups["data"].Value : s;
        b64 = Regex.Replace(b64, @"\s+", "");
        try { return Convert.FromBase64String(b64); }
        catch { return null; }
    }

    private static DateTime? ParseDateYmd(string? ymd)
    {
        if (string.IsNullOrWhiteSpace(ymd)) return null;
        if (DateTime.TryParseExact(ymd.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var dt)) return dt.Date;
        return null;
    }

    // ======================================================================
    // A) EXISTE (correo/nickname/teléfono via SP)  → /api/auth/exists
    // ======================================================================
    [HttpGet("exists")]
    public async Task<IActionResult> Exists([FromQuery] string credential)
    {
        if (string.IsNullOrWhiteSpace(credential))
            return BadRequest(new { message = "credential es requerido" });

        await using var conn = new MySqlConnection(Cs);
        await conn.OpenAsync();

        await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn)
        { CommandType = CommandType.StoredProcedure };
        cmd.Parameters.AddWithValue("p_Identificador", credential.Trim());

        await using var rd = await cmd.ExecuteReaderAsync();
        if (!rd.Read()) return Ok(new { exists = false });

        var id     = Convert.ToUInt64(rd["UsuarioId"]);
        var active = Convert.ToInt32(rd["EstaActivo"]) == 1;
        return Ok(new { exists = true, id, active });
    }

    // ======================================================================
    // B) LOGIN (credenciales)  → /api/auth/login
    // ======================================================================
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Credential) || string.IsNullOrWhiteSpace(dto.Password))
            return Ok(new { success = false, message = "Credenciales requeridas" });

        try
        {
            await using var conn = new MySqlConnection(Cs);
            await conn.OpenAsync();

            await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn)
            { CommandType = CommandType.StoredProcedure };
            cmd.Parameters.AddWithValue("p_Identificador", dto.Credential.Trim());

            await using var rd = await cmd.ExecuteReaderAsync();
            if (!rd.Read())
                return Ok(new { success = false, message = "Usuario no encontrado" });

            var id     = Convert.ToUInt64(rd["UsuarioId"]);
            var phc    = rd["PasswordHash"] as string ?? "";
            var activo = Convert.ToInt32(rd["EstaActivo"]) == 1;
            if (!activo) return Ok(new { success = false, message = "Usuario inactivo" });

            bool ok = !string.IsNullOrWhiteSpace(phc) &&
                      phc.StartsWith("$2") && BCrypt.Net.BCrypt.Verify(dto.Password, phc);

            return ok
                ? Ok(new { success = true, message = "OK", id })
                : Ok(new { success = false, message = "Credenciales inválidas" });
        }
        catch (Exception ex)
        {
            var errId = Guid.NewGuid().ToString("N")[..8];
            _log.LogError(ex, "Login failed ({ErrId})", errId);
            return Problem("Error al iniciar sesión. ERR-" + errId, statusCode: 500);
        }
    }

    // ======================================================================
    // C) REGISTER (guarda Fotografia y Fotografia2) → /api/auth/register
    //     Responde también con verificación de lo que llegó y lo que quedó en BD
    // ======================================================================
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterDto dto, CancellationToken ct)
    {
        // Validaciones mínimas
        if (string.IsNullOrWhiteSpace(dto.Email) ||
            string.IsNullOrWhiteSpace(dto.Nickname) ||
            string.IsNullOrWhiteSpace(dto.Password))
        {
            return BadRequest(new { success = false, message = "Email, Nickname y Password son obligatorios." });
        }

        // Hash de contraseña (BCrypt)
        var phc = BCrypt.Net.BCrypt.HashPassword(dto.Password, workFactor: 12);

        // Procesar fotos
        var foto1 = DecodeDataUrlToBytes(dto.PhotoBase64);   // puede ser null
        var foto2 = DecodeDataUrlToBytes(dto.Photo2Base64);  // puede ser null

        var recv1 = foto1?.Length ?? 0;
        var recv2 = foto2?.Length ?? 0;

        var mime1 = string.IsNullOrWhiteSpace(dto.PhotoMime) ? null : dto.PhotoMime!.Trim();
        var mime2 = string.IsNullOrWhiteSpace(dto.Photo2Mime) ? null : dto.Photo2Mime!.Trim();

        // Fecha
        var fecha = ParseDateYmd(dto.Birthdate);

        try
        {
            ulong newId = 0;
            int code = -1;
            string msg = "OK";

            await using (var conn = new MySqlConnection(Cs))
            {
                await conn.OpenAsync(ct);

                await using var cmd = new MySqlCommand("sp_usuarios_crear", conn)
                { CommandType = CommandType.StoredProcedure };

                // El orden de parámetros coincide con el SP que compartiste
                cmd.Parameters.Add("p_Email",           MySqlDbType.VarChar, 254).Value = dto.Email!.Trim();
                cmd.Parameters.Add("p_Telefono",        MySqlDbType.VarChar, 32).Value  =
                    string.IsNullOrWhiteSpace(dto.Phone) ? DBNull.Value : dto.Phone!.Trim();
                cmd.Parameters.Add("p_FechaNacimiento", MySqlDbType.Date).Value        =
                    (object?)fecha ?? DBNull.Value;
                cmd.Parameters.Add("p_Nickname",        MySqlDbType.VarChar, 50).Value  = dto.Nickname!.Trim();
                cmd.Parameters.Add("p_PasswordPHC",     MySqlDbType.VarChar, 255).Value = phc;

                cmd.Parameters.Add("p_Fotografia",      MySqlDbType.MediumBlob).Value   =
                    (object?)foto1 ?? DBNull.Value;
                cmd.Parameters.Add("p_FotografiaMime",  MySqlDbType.VarChar, 64).Value  =
                    (object?)mime1 ?? DBNull.Value;

                cmd.Parameters.Add("p_Fotografia2",     MySqlDbType.MediumBlob).Value   =
                    (object?)foto2 ?? DBNull.Value;
                cmd.Parameters.Add("p_Fotografia2Mime", MySqlDbType.VarChar, 64).Value  =
                    (object?)mime2 ?? DBNull.Value;

                cmd.Parameters.Add("p_RolId",           MySqlDbType.UByte).Value        = dto.RoleId ?? 2;

                var pUsuarioId = new MySqlParameter("p_UsuarioId", MySqlDbType.UInt64)
                { Direction = ParameterDirection.Output };
                var pCodigo    = new MySqlParameter("p_Codigo",    MySqlDbType.Int32)
                { Direction = ParameterDirection.Output };
                var pMensaje   = new MySqlParameter("p_Mensaje",   MySqlDbType.VarChar, 255)
                { Direction = ParameterDirection.Output };

                cmd.Parameters.Add(pUsuarioId);
                cmd.Parameters.Add(pCodigo);
                cmd.Parameters.Add(pMensaje);

                await cmd.ExecuteNonQueryAsync(ct);

                code = (pCodigo.Value is int ic) ? ic : Convert.ToInt32(pCodigo.Value ?? 0);
                msg  = pMensaje.Value?.ToString() ?? "OK";
                newId = pUsuarioId.Value is ulong ul ? ul : Convert.ToUInt64(pUsuarioId.Value ?? 0UL);

                if (code != 0)
                {
                    return code switch
                    {
                        409 => Conflict(new { success = false, message = msg, recv1, recv2 }),
                        413 => StatusCode(413, new { success = false, message = msg, recv1, recv2 }),
                        400 => BadRequest(new { success = false, message = msg, recv1, recv2 }),
                        422 => UnprocessableEntity(new { success = false, message = msg, recv1, recv2 }),
                        _ => BadRequest(new { success = false, message = msg, code, recv1, recv2 })
                    };
                }

                // Verificación inmediata en BD (OCTET_LENGTH de ambos blobs)
                int dbLen1 = 0, dbLen2 = 0;
                await using (var cmd2 = new MySqlCommand(@"
                    SELECT COALESCE(OCTET_LENGTH(Fotografia),0) AS L1,
                           COALESCE(OCTET_LENGTH(Fotografia2),0) AS L2
                    FROM usuarios
                    WHERE UsuarioId = @id
                    LIMIT 1;", conn))
                {
                    cmd2.Parameters.AddWithValue("@id", (long)newId);
                    await using var rd = await cmd2.ExecuteReaderAsync(ct);
                    if (await rd.ReadAsync(ct))
                    {
                        dbLen1 = Convert.ToInt32(rd["L1"]);
                        dbLen2 = Convert.ToInt32(rd["L2"]);
                    }
                }

                return Ok(new
                {
                    success = true,
                    id = newId,
                    message = msg,
                    recvPhoto1Bytes = recv1,
                    recvPhoto2Bytes = recv2,
                    dbPhoto1Bytes = dbLen1,
                    dbPhoto2Bytes = dbLen2
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error en registro");
            return StatusCode(500, new { success = false, message = "Error interno", detail = ex.Message, recv1, recv2 });
        }
    }

    // ======================================================================
    // D) LOGIN FACIAL - FAST (recorre 75 como tu Python; escoge la mejor)
    // ======================================================================
    [HttpPost("login-facial-fast")]
    public async Task<IActionResult> LoginFacialFast([FromBody] LoginFacialFastRequest dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.PhotoBase64))
            return Ok(new { success = false, matched = false, message = "Foto requerida" });

        var rostroA = StripDataUrlOrReturn(dto.PhotoBase64);
        if (string.IsNullOrWhiteSpace(rostroA))
            return Ok(new { success = false, matched = false, message = "Foto inválida" });

        var minPct = Math.Clamp(dto.MinPercent, 1, 100);
        var sw = Stopwatch.StartNew();

        // 1) Construir la lista de candidatos (1 por UsuarioId/Credential, o 75 recientes)
        var candidatos = new List<(ulong Id, string Email, string Nickname, byte[] Foto)>();

        await using (var conn = new MySqlConnection(Cs))
        {
            await conn.OpenAsync(ct);

            if ((dto.UsuarioId ?? dto.UserId) is ulong uid && uid > 0)
            {
                var sql = @"
                    SELECT UsuarioId, Email, Nickname, COALESCE(Fotografia, Fotografia2) AS Foto
                    FROM usuarios
                    WHERE UsuarioId=@id
                      AND COALESCE(OCTET_LENGTH(Fotografia),0)+COALESCE(OCTET_LENGTH(Fotografia2),0) > 0
                    LIMIT 1";
                await using var cmd = new MySqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("@id", (long)uid);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    candidatos.Add((Convert.ToUInt64(rd["UsuarioId"]),
                                    rd["Email"]?.ToString() ?? "",
                                    rd["Nickname"]?.ToString() ?? "",
                                    (byte[])rd["Foto"]));
            }
            else if (!string.IsNullOrWhiteSpace(dto.Credential))
            {
                await using var cmd = new MySqlCommand("sp_usuarios_obtener_por_credencial", conn)
                { CommandType = CommandType.StoredProcedure };
                cmd.Parameters.AddWithValue("p_Identificador", dto.Credential!.Trim());
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                if (await rd.ReadAsync(ct))
                {
                    var id = Convert.ToUInt64(rd["UsuarioId"]);
                    await rd.CloseAsync();

                    var sql = @"
                        SELECT UsuarioId, Email, Nickname, COALESCE(Fotografia, Fotografia2) AS Foto
                        FROM usuarios
                        WHERE UsuarioId=@id
                          AND COALESCE(OCTET_LENGTH(Fotografia),0)+COALESCE(OCTET_LENGTH(Fotografia2),0) > 0
                        LIMIT 1";
                    await using var cmd2 = new MySqlCommand(sql, conn);
                    cmd2.Parameters.AddWithValue("@id", (long)id);
                    await using var rd2 = await cmd2.ExecuteReaderAsync(ct);
                    while (await rd2.ReadAsync(ct))
                        candidatos.Add((Convert.ToUInt64(rd2["UsuarioId"]),
                                        rd2["Email"]?.ToString() ?? "",
                                        rd2["Nickname"]?.ToString() ?? "",
                                        (byte[])rd2["Foto"]));
                }
            }
            else
            {
                var sql = @"
                    SELECT UsuarioId, Email, Nickname, COALESCE(Fotografia, Fotografia2) AS Foto
                    FROM usuarios
                    WHERE COALESCE(OCTET_LENGTH(Fotografia),0)+COALESCE(OCTET_LENGTH(Fotografia2),0) > 0
                    ORDER BY UsuarioId DESC
                    LIMIT 75"; // como tu Python
                await using var cmd = new MySqlCommand(sql, conn);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    candidatos.Add((Convert.ToUInt64(rd["UsuarioId"]),
                                    rd["Email"]?.ToString() ?? "",
                                    rd["Nickname"]?.ToString() ?? "",
                                    (byte[])rd["Foto"]));
            }
        }

        if (candidatos.Count == 0)
            return Ok(new { success = false, matched = false, message = "No hay candidatos con foto" });

        // 2) Comparar SECUENCIALMENTE (rápido y estable) como tu Python fast
        var http = _httpFactory.CreateClient("faceVerify");
        var mejores = new List<(ulong Id, string Email, string Nickname, int Score, double Percent, string? Status)>();

        foreach (var c in candidatos)
        {
            try
            {
                var req = new FaceVerifyRequest(rostroA, BlobToBase64OrUseAsIs(c.Foto));
                var resp = await http.PostAsJsonAsync("api/Rostro/Verificar", req, ct);
                if (!resp.IsSuccessStatusCode) continue;

                var data = await resp.Content.ReadFromJsonAsync<FaceVerifyResponse>(cancellationToken: ct);
                if (data is null || !data.resultado) continue;

                int score = 0; _ = int.TryParse(data.score ?? "0", out score);
                var percent = Math.Min(100.0, score / 2.0);
                if (data.coincide)
                {
                    mejores.Add((c.Id, c.Email, c.Nickname, score, percent, data.status));
                }
            }
            catch
            {
                // ignora errores individuales
            }
        }

        if (mejores.Count == 0)
            return Ok(new { success = false, matched = false, message = "Sin coincidencias" });

        var best = mejores.OrderByDescending(r => r.Score).First();
        var matched = best.Percent >= minPct;

        return Ok(new
        {
            success = matched,
            matched,
            userId = best.Id,
            email = best.Email,
            nickname = best.Nickname,
            score = best.Score,
            percent = best.Percent,
            providerStatus = best.Status,
            candidatesTried = candidatos.Count,
            elapsedMs = sw.ElapsedMilliseconds
        });
    }

    // ======================================================================
    // E) LOGIN FACIAL - Escaneo multi-candidato (paralelo)
    // ======================================================================
    [HttpPost("login-facial")]
    public async Task<IActionResult> LoginFacial([FromBody] LoginFacialDto dto, CancellationToken ct)
    {
        try
        {
            var rostroA = StripDataUrlOrReturn(dto.PhotoBase64);
            if (string.IsNullOrWhiteSpace(rostroA))
                return Ok(new { success = false, matched = false, message = "Foto requerida" });

            var max = Math.Clamp(dto.MaxCandidates, 1, 500);
            var dop = Math.Clamp(dto.DegreeOfParallelism, 1, 32);
            var minPct = Math.Clamp(dto.MinPercent, 1, 100);

            var candidatos = new List<(ulong Id, string Email, string Nickname, byte[] Foto)>();
            await using (var conn = new MySqlConnection(Cs))
            {
                await conn.OpenAsync(ct);
                var sql = $@"
                    SELECT UsuarioId, Email, Nickname, COALESCE(Fotografia, Fotografia2) AS Foto
                    FROM usuarios
                    WHERE COALESCE(OCTET_LENGTH(Fotografia),0)+COALESCE(OCTET_LENGTH(Fotografia2),0) > 0
                    ORDER BY UsuarioId DESC
                    LIMIT {max}";
                await using var cmd = new MySqlCommand(sql, conn);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    candidatos.Add((Convert.ToUInt64(rd["UsuarioId"]),
                                    rd["Email"]?.ToString() ?? "",
                                    rd["Nickname"]?.ToString() ?? "",
                                    (byte[])rd["Foto"]));
            }

            if (candidatos.Count == 0)
                return Ok(new { success = false, matched = false, message = "No hay candidatos con foto" });

            var http = _httpFactory.CreateClient("faceVerify");
            var resultados = new ConcurrentBag<(ulong Id, string Email, string Nickname, int Score, double Percent, string? Status)>();

            await Parallel.ForEachAsync(candidatos,
                new ParallelOptions { MaxDegreeOfParallelism = dop, CancellationToken = ct },
                async (c, token) =>
                {
                    try
                    {
                        var req = new FaceVerifyRequest(rostroA, BlobToBase64OrUseAsIs(c.Foto));
                        using var resp = await http.PostAsJsonAsync("api/Rostro/Verificar", req, token);
                        if (!resp.IsSuccessStatusCode) return;

                        var data = await resp.Content.ReadFromJsonAsync<FaceVerifyResponse>(cancellationToken: token);
                        if (data is null || !data.resultado) return;

                        int score = 0; _ = int.TryParse(data.score ?? "0", out score);
                        var percent = Math.Min(100.0, score / 2.0);
                        if (data.coincide) resultados.Add((c.Id, c.Email, c.Nickname, score, percent, data.status));
                    }
                    catch { /* ignora individuales */ }
                });

            if (resultados.IsEmpty)
                return Ok(new { success = false, matched = false, message = "Sin coincidencias" });

            var best = resultados.OrderByDescending(r => r.Score).First();
            var matched = best.Percent >= minPct;

            return Ok(new
            {
                success = matched,
                matched,
                userId = best.Id,
                email = best.Email,
                nickname = best.Nickname,
                score = best.Score,
                percent = best.Percent,
                providerStatus = best.Status,
                candidatesTried = candidatos.Count
            });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error en login-facial");
            return StatusCode(500, new { success = false, message = "Error interno", detail = ex.Message });
        }
    }
}
