using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace BaseUsuarios.Api.Endpoints
{
    public static class PersonalizationEndpoints
    {
        public static IEndpointRouteBuilder MapPersonalizationEndpoints(this IEndpointRouteBuilder app)
        {
            var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();
            string GetCsLocal() => cfg.GetConnectionString("ComprasLocal") ?? cfg.GetConnectionString("Default")!;

            var api = app.MapGroup("/api");
            var local = api.MapGroup("/local");

            // =======================
            // 1) CREAR/OBTENER PERSONALIZACIÓN (por carrito_item + lado)
            // =======================
            local.MapPost("/personalizations", async (HttpRequest req) =>
            {
                var dto = await System.Text.Json.JsonSerializer.DeserializeAsync<CreatePersoDto>(req.Body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (dto is null || dto.carritoItemId <= 0 || (dto.lado != "A" && dto.lado != "B"))
                    return Results.BadRequest(new { message = "Datos inválidos" });

                await using var conn = new MySqlConnection(GetCsLocal());
                await conn.OpenAsync();

                // intentar recuperar primero
                await using (var cmdSel = new MySqlCommand(@"
                    SELECT id FROM personalizaciones
                    WHERE propietario_tipo='carrito_item' AND propietario_id=@ci AND lado=@lado
                    LIMIT 1;", conn))
                {
                    cmdSel.Parameters.AddWithValue("@ci", dto.carritoItemId);
                    cmdSel.Parameters.AddWithValue("@lado", dto.lado);
                    var o = await cmdSel.ExecuteScalarAsync();
                    if (o != null) return Results.Ok(new { id = Convert.ToInt64(o) });
                }

                long id;
                await using (var cmdIns = new MySqlCommand(@"
                    INSERT INTO personalizaciones (propietario_tipo, propietario_id, lado, captura)
                    VALUES ('carrito_item', @ci, @lado, NULL);
                    SELECT LAST_INSERT_ID();", conn))
                {
                    cmdIns.Parameters.AddWithValue("@ci", dto.carritoItemId);
                    cmdIns.Parameters.AddWithValue("@lado", dto.lado);
                    id = Convert.ToInt64(await cmdIns.ExecuteScalarAsync());
                }

                return Results.Ok(new { id });
            });

            // =======================
            // 2) SUBIR ARCHIVO (multipart) → crea registro en "archivos"
            // =======================
            local.MapPost("/uploads", async (HttpRequest req, IHostEnvironment env) =>
            {
                if (!req.HasFormContentType) return Results.BadRequest(new { message = "Content-Type inválido" });
                var form = await req.ReadFormAsync();
                if (!long.TryParse(form["personalizationId"], out long personalizationId))
                    return Results.BadRequest(new { message = "personalizationId requerido" });

                var file = form.Files["file"];
                if (file is null || file.Length == 0) return Results.BadRequest(new { message = "Archivo vacío" });

                var uploadsRoot = Path.Combine(env.ContentRootPath, "wwwroot", "uploads");
                Directory.CreateDirectory(uploadsRoot);

                var ext = Path.GetExtension(file.FileName);
                var name = $"{Guid.NewGuid():N}{ext}";
                var full = Path.Combine(uploadsRoot, name);
                await using (var fs = new FileStream(full, FileMode.Create)) await file.CopyToAsync(fs);

                var url = $"/uploads/{name}";
                long archivoId;

                await using var conn = new MySqlConnection(GetCsLocal());
                await conn.OpenAsync();
                await using (var cmd = new MySqlCommand(@"
                    INSERT INTO archivos (tipo, ruta, mime, propietario_tipo, propietario_id, creado_en)
                    VALUES ('foto', @ruta, @mime, 'personalizacion', @pid, NOW());
                    SELECT LAST_INSERT_ID();", conn))
                {
                    cmd.Parameters.AddWithValue("@ruta", url);
                    cmd.Parameters.AddWithValue("@mime", file.ContentType ?? "image/*");
                    cmd.Parameters.AddWithValue("@pid", personalizationId);
                    archivoId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                return Results.Ok(new { archivoId, url });
            });

            // =======================
            // 3) AGREGAR CAPA A LA PERSONALIZACIÓN
            // =======================
            local.MapPost("/personalizations/{id:long}/layers", async (long id, HttpRequest req) =>
            {
                var dto = await System.Text.Json.JsonSerializer.DeserializeAsync<LayerDto>(req.Body,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (dto is null || string.IsNullOrWhiteSpace(dto.tipo_capa))
                    return Results.BadRequest(new { message = "tipo_capa requerido" });

                if (dto.tipo_capa == "sticker" && dto.sticker_id is null)
                    return Results.BadRequest(new { message = "sticker_id requerido" });
                if (dto.tipo_capa == "foto" && dto.archivo_id is null)
                    return Results.BadRequest(new { message = "archivo_id requerido" });

                long layerId;
                await using var conn = new MySqlConnection(GetCsLocal());
                await conn.OpenAsync();

                await using (var cmd = new MySqlCommand(@"
                    INSERT INTO personalizacion_capas
                      (personalizacion_id, tipo_capa, z_index, pos_x, pos_y, escala, rotacion,
                       texto, fuente, color, archivo_id, sticker_id, filtro_id, datos)
                    VALUES
                      (@pid, @tipo, @z, @x, @y, @esc, @rot, @txt, @fnt, @col, @arch, @stk, @fil, @datos);
                    SELECT LAST_INSERT_ID();", conn))
                {
                    cmd.Parameters.AddWithValue("@pid", id);
                    cmd.Parameters.AddWithValue("@tipo", dto.tipo_capa);
                    cmd.Parameters.AddWithValue("@z", dto.z_index ?? 0);
                    cmd.Parameters.AddWithValue("@x", dto.pos_x ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@y", dto.pos_y ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@esc", dto.escala ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@rot", dto.rotacion ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@txt", dto.texto ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@fnt", dto.fuente ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@col", dto.color ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@arch", dto.archivo_id ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@stk", dto.sticker_id ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@fil", dto.filtro_id ?? (object)DBNull.Value);
                    cmd.Parameters.AddWithValue("@datos", dto.datos ?? (object)DBNull.Value);

                    layerId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
                }

                return Results.Ok(new { id = layerId });
            });

            // =======================
            // 4) LISTAR CAPAS (preview)
            //    - ?simple=1 → SELECT sin joins (robusto)
            // =======================
            local.MapGet("/personalizations/{id:long}/layers", async (long id, HttpRequest req) =>
            {
                bool simple = string.Equals(req.Query["simple"], "1", StringComparison.OrdinalIgnoreCase);

                try
                {
                    var list = new List<object>();
                    await using var conn = new MySqlConnection(GetCsLocal());
                    await conn.OpenAsync();

                    string sql = simple
                        ? @"
                            SELECT 
                              pc.id, pc.tipo_capa, pc.z_index, pc.pos_x, pc.pos_y, pc.escala, pc.rotacion,
                              pc.texto, pc.fuente, pc.color,
                              pc.archivo_id, NULL AS archivo_url,
                              pc.sticker_id, NULL AS sticker_nombre, NULL AS sticker_url,
                              pc.filtro_id, NULL AS filtro_nombre
                            FROM personalizacion_capas pc
                            WHERE pc.personalizacion_id = @pid
                            ORDER BY COALESCE(pc.z_index,0), pc.id;"
                        : @"
                            SELECT 
                              pc.id, pc.tipo_capa, pc.z_index, pc.pos_x, pc.pos_y, pc.escala, pc.rotacion,
                              pc.texto, pc.fuente, pc.color,
                              pc.archivo_id, a.ruta AS archivo_url,
                              pc.sticker_id, s.nombre AS sticker_nombre, a2.ruta AS sticker_url,
                              pc.filtro_id, f.nombre AS filtro_nombre
                            FROM personalizacion_capas pc
                            LEFT JOIN archivos a  ON a.id  = pc.archivo_id
                            LEFT JOIN stickers s   ON s.id  = pc.sticker_id
                            LEFT JOIN archivos a2  ON a2.id = s.archivo_id
                            LEFT JOIN filtros  f   ON f.id  = pc.filtro_id
                            WHERE pc.personalizacion_id = @pid
                            ORDER BY COALESCE(pc.z_index,0), pc.id;";

                    await using var cmd = new MySqlCommand(sql, conn);
                    cmd.Parameters.AddWithValue("@pid", id);

                    await using var rd = await cmd.ExecuteReaderAsync();
                    while (await rd.ReadAsync())
                    {
                        long? GetInt64N(string col) => rd[col] == DBNull.Value ? (long?)null : Convert.ToInt64(rd[col]);
                        long  GetInt64 (string col) => rd[col] == DBNull.Value ? 0L : Convert.ToInt64(rd[col]);
                        int   GetInt32 (string col) => rd[col] == DBNull.Value ? 0  : Convert.ToInt32(rd[col]);
                        decimal? GetDec(string col) => rd[col] == DBNull.Value ? (decimal?)null : Convert.ToDecimal(rd[col]);
                        string? GetStr(string col) => rd[col] == DBNull.Value ? null : Convert.ToString(rd[col]);

                        list.Add(new
                        {
                            id             = GetInt64("id"),
                            tipo_capa      = GetStr("tipo_capa")!,
                            z_index        = GetInt32("z_index"),
                            pos_x          = GetDec("pos_x"),
                            pos_y          = GetDec("pos_y"),
                            escala         = GetDec("escala"),
                            rotacion       = GetDec("rotacion"),
                            texto          = GetStr("texto"),
                            fuente         = GetStr("fuente"),
                            color          = GetStr("color"),
                            archivo_id     = GetInt64N("archivo_id"),
                            archivo_url    = GetStr("archivo_url"),
                            sticker_id     = GetInt64N("sticker_id"),
                            sticker_nombre = GetStr("sticker_nombre"),
                            sticker_url    = GetStr("sticker_url"),
                            filtro_id      = GetInt64N("filtro_id"),
                            filtro_nombre  = GetStr("filtro_nombre")
                        });
                    }

                    return Results.Ok(list);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Error leyendo capas", detail: ex.Message, statusCode: 500);
                }
            });

            // =======================
            // 5) LISTAR STICKERS
            // =======================
            local.MapGet("/stickers", async () =>
            {
                var list = new List<object>();
                await using var conn = new MySqlConnection(GetCsLocal());
                await conn.OpenAsync();

                var sql = @"
                    SELECT s.id, s.nombre, a.ruta AS url
                    FROM stickers s
                    LEFT JOIN archivos a ON a.id = s.archivo_id
                    ORDER BY s.id;";
                await using var cmd = new MySqlCommand(sql, conn);
                await using var rd = await cmd.ExecuteReaderAsync();
                while (await rd.ReadAsync())
                {
                    list.Add(new
                    {
                        id     = Convert.ToInt64(rd["id"]),
                        nombre = rd["nombre"] == DBNull.Value ? null : Convert.ToString(rd["nombre"]),
                        url    = rd["url"] == DBNull.Value ? null : Convert.ToString(rd["url"])
                    });
                }
                return Results.Ok(list);
            });

            // =======================
            // 6) SEED rápido: crea 1 sticker (usa /images/llavero-sticker.png)
            // =======================
            local.MapPost("/stickers/seed-default", async (IHostEnvironment env) =>
            {
                var ruta = "/images/llavero-sticker.png"; // coloca ese png en wwwroot/images
                long archivoId;
                long stickerId;

                await using var conn = new MySqlConnection(GetCsLocal());
                await conn.OpenAsync();

                await using (var cmdA = new MySqlCommand(@"
                    INSERT INTO archivos (tipo, ruta, mime, creado_en)
                    VALUES ('sticker', @ruta, 'image/png', NOW());
                    SELECT LAST_INSERT_ID();", conn))
                {
                    cmdA.Parameters.AddWithValue("@ruta", ruta);
                    archivoId = Convert.ToInt64(await cmdA.ExecuteScalarAsync());
                }

                await using (var cmdS = new MySqlCommand(@"
                    INSERT INTO stickers (nombre, archivo_id)
                    VALUES ('Sticker básico', @aid);
                    SELECT LAST_INSERT_ID();", conn))
                {
                    cmdS.Parameters.AddWithValue("@aid", archivoId);
                    stickerId = Convert.ToInt64(await cmdS.ExecuteScalarAsync());
                }

                return Results.Ok(new { archivoId, stickerId, ruta });
            });

            // =======================
            // 7) DIAGNÓSTICO BD (tablas/conteos y existencia de la personalización)
            // =======================
            local.MapGet("/diag/personalization/{id:long}", async (long id) =>
            {
                try
                {
                    var info = new Dictionary<string, object?>();
                    await using var conn = new MySqlConnection(GetCsLocal());
                    await conn.OpenAsync();

                    async Task<bool> TableExists(string table)
                    {
                        await using var cmd = new MySqlCommand(
                            "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @t;", conn);
                        cmd.Parameters.AddWithValue("@t", table);
                        var n = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                        return n > 0;
                    }

                    async Task<long> Count(string table)
                    {
                        await using var cmd = new MySqlCommand($"SELECT COUNT(*) FROM {table};", conn);
                        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
                    }

                    var tablas = new[] { "personalizaciones", "personalizacion_capas", "archivos", "stickers", "filtros" };
                    var exist = new Dictionary<string, bool>();
                    foreach (var t in tablas)
                        exist[t] = await TableExists(t);

                    info["tablas"] = exist;

                    var counts = new Dictionary<string, object>();
                    foreach (var t in tablas)
                        counts[t] = exist[t] ? await Count(t) : "NO_EXISTE";
                    info["conteos"] = counts;

                    if (exist["personalizaciones"])
                    {
                        await using var cmd = new MySqlCommand(
                            "SELECT COUNT(*) FROM personalizaciones WHERE id=@id;", conn);
                        cmd.Parameters.AddWithValue("@id", id);
                        info["personalizacion_existe"] = Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
                    }
                    else info["personalizacion_existe"] = false;

                    return Results.Ok(info);
                }
                catch (Exception ex)
                {
                    return Results.Problem(title: "Diag error", detail: ex.Message, statusCode: 500);
                }
            });

            return app;
        }

        public record CreatePersoDto(long carritoItemId, string lado);

        public record LayerDto(
            string tipo_capa, int? z_index, decimal? pos_x, decimal? pos_y, decimal? escala, decimal? rotacion,
            string? texto, string? fuente, string? color, long? archivo_id, long? sticker_id, long? filtro_id, string? datos
        );
    }
}
