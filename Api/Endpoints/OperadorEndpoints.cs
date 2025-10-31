// Api/Endpoints/OperadorEndpoints.cs
using Dapper;
using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;
using System.Data;

namespace Api.Endpoints;

public static class OperadorEndpoints
{
    public static IEndpointRouteBuilder MapOperadorEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/operator").WithTags("Operator");

        // GET /api/operator/orders?estado=CRE&limit=50
        g.MapGet("/orders", GetOrders);

        // POST /api/operator/orders/{orderId}/advance   body: { to: "PROC", note?: string }
        g.MapPost("/orders/{orderId:long}/advance", AdvanceOrder);

        return app;
    }

    /* ================= ConnStrings ================= */
    static string GetConnCompras(IConfiguration cfg)
        => cfg.GetConnectionString("ComprasLocal")
           ?? throw new InvalidOperationException("Falta ConnectionStrings:ComprasLocal");

    static string? GetConnUsuarios(IConfiguration cfg)
        => cfg.GetConnectionString("Default");

    /* ================= DTOs ================= */
    public record OperatorCardDto(
        string folio,         // folio de la orden
        string customerName,  // Nickname (o Email)
        string nfcType,       // "Link"
        string? nfcData,      // o.qr_texto
        string? imageA,       // /uploads/...
        string? imageB        // /uploads/...
    );

    /* ================= GET Orders =================
       Devuelve una “lista aplanada”: 1 tarjeta por cada item de la orden
    */
    static async Task<IResult> GetOrders(
        [FromServices] IConfiguration cfg,
        [FromQuery] string? estado = "CRE",
        [FromQuery] int limit = 50)
    {
        await using var db = new MySqlConnection(GetConnCompras(cfg));
        var p = new DynamicParameters();
        p.Add("@estado", estado);
        p.Add("@limit", limit);

        // 1) Ordenes por estado
        const string sqlOrders = @"
SELECT o.id, o.folio, o.usuario_id, o.qr_texto, o.creado_en
FROM ordenes o
JOIN procesos pr  ON pr.id = o.proceso_id AND pr.codigo = 'ORD'
JOIN estados est  ON est.id = o.estado_actual_id AND est.codigo = @estado
ORDER BY o.creado_en DESC
LIMIT @limit;";
        var orders = (await db.QueryAsync(sqlOrders, p)).ToList();

        var cards = new List<OperatorCardDto>();

        foreach (var o in orders)
        {
            // 2) Items de la orden
            const string sqlItems = @"
SELECT 
  oi.id           AS orden_item_id,
  oi.producto_id,
  p.nombre        AS producto,
  oi.cantidad,
  oi.precio_unitario,
  oi.personalizacion_ladoA_id AS pAId,
  oi.personalizacion_ladoB_id AS pBId
FROM orden_items oi
JOIN productos p ON p.id = oi.producto_id
WHERE oi.orden_id = @ordenId;";
            var items = await db.QueryAsync(sqlItems, new { ordenId = (long)o.id });

            // 3) Nombre del usuario desde la BD de usuarios (Default)
            var userName = await GetUserDisplayNameAsync(cfg, (long)o.usuario_id)
                           ?? $"Cliente #{o.usuario_id}";

            foreach (var it in items)
            {
                // Primera imagen 'foto' de cada lado
                var imageA = await GetFirstPhotoAsync(db, (long?)it.pAId);
                var imageB = await GetFirstPhotoAsync(db, (long?)it.pBId);

                // NFC/QR
                var nfcType = "Link";
                string? nfcData = (string?)o.qr_texto;

                string folio = Convert.ToString(o.folio) ?? ((long)o.id).ToString();

                cards.Add(new OperatorCardDto(
                    folio: folio,
                    customerName: userName,
                    nfcType: nfcType,
                    nfcData: nfcData,
                    imageA: imageA,
                    imageB: imageB
                ));
            }
        }

        return Results.Ok(new { items = cards });
    }

    /* ========== Helper: primera /uploads/... de capa 'foto' ========== */
    static async Task<string?> GetFirstPhotoAsync(IDbConnection db, long? personalizacionId)
    {
        if (personalizacionId == null) return null;
        const string sql = @"
SELECT a.ruta
FROM personalizacion_capas c
JOIN archivos a ON a.id = c.archivo_id
WHERE c.personalizacion_id = @pid AND c.tipo_capa = 'foto'
ORDER BY COALESCE(c.z_index,0) DESC, c.id DESC
LIMIT 1;";
        var ruta = await db.ExecuteScalarAsync<string?>(sql, new { pid = personalizacionId });
        if (string.IsNullOrWhiteSpace(ruta)) return null;
        return ruta.StartsWith("/") ? ruta : "/" + ruta;
    }

    /* ========== Helper: display name del usuario ==========
       1) INTENTA SELECT directo a tabla `usuarios`:
            name = COALESCE(NULLIF(TRIM(Nickname),''), NULLIF(TRIM(Email), ''))
       2) Si falla por permisos, intenta el SP: CALL sp_usuarios_obtener_por_id(@id)
    */
    static async Task<string?> GetUserDisplayNameAsync(IConfiguration cfg, long userId)
    {
        var cs = GetConnUsuarios(cfg);
        if (string.IsNullOrWhiteSpace(cs)) return null;

        // 1) SELECT directo
        try
        {
            await using var db = new MySqlConnection(cs);
            const string sql = @"
SELECT
  COALESCE(
    NULLIF(TRIM(Nickname),''),
    NULLIF(TRIM(Email),'')
  ) AS name
FROM usuarios
WHERE UsuarioId = @id
LIMIT 1;";
            var name = await db.ExecuteScalarAsync<string?>(sql, new { id = userId });
            if (!string.IsNullOrWhiteSpace(name)) return name!.Trim();
        }
        catch
        {
            // sigue al SP
        }

        // 2) SP de respaldo
        try
        {
            await using var db2 = new MySqlConnection(cs);
            // El SP devuelve 1 fila con columnas (UsuarioId, Email, Telefono, FechaNacimiento, Nickname, PasswordHash, RolId, EstaActivo)
            var row = await db2.QueryFirstOrDefaultAsync<dynamic>(
                "CALL sp_usuarios_obtener_por_id(@uid);", new { uid = userId });

            if (row != null)
            {
                string? nick = (row.Nickname as string) ?? (row.nickname as string);
                string? mail = (row.Email as string) ?? (row.email as string);
                var name = !string.IsNullOrWhiteSpace(nick) ? nick!.Trim()
                         : !string.IsNullOrWhiteSpace(mail) ? mail!.Trim()
                         : null;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
        }
        catch
        {
            // ignora -> caerá a null
        }

        return null;
    }

    /* ================= Avanzar estado ================= */
    public record AdvanceReq(string to, string? note);

    static async Task<IResult> AdvanceOrder(
        [FromServices] IConfiguration cfg,
        long orderId,
        [FromBody] AdvanceReq body)
    {
        if (string.IsNullOrWhiteSpace(body.to))
            return Results.BadRequest(new { message = "Campo 'to' requerido (PROC|READY)" });

        await using var db = new MySqlConnection(GetConnCompras(cfg));
        await db.OpenAsync();
        await using var tx = await db.BeginTransactionAsync();

        // Estado destino por código
        const string sqlGetNext = @"
SELECT e.id FROM estados e 
JOIN procesos p ON p.id = e.proceso_id AND p.codigo='ORD'
WHERE e.codigo = @toCode;";
        var destId = await db.ExecuteScalarAsync<long?>(
            new CommandDefinition(sqlGetNext, new { toCode = body.to }, transaction: tx));
        if (destId == null) return Results.BadRequest(new { message = "Estado destino no existe" });

        // Validar transición
        const string sqlCheck = @"
SELECT COUNT(*) FROM transiciones t
JOIN ordenes o ON o.proceso_id = t.proceso_id
WHERE o.id=@orderId AND t.estado_desde_id = o.estado_actual_id AND t.estado_hasta_id = @destId;";
        var ok = await db.ExecuteScalarAsync<int>(
            new CommandDefinition(sqlCheck, new { orderId, destId }, transaction: tx));
        if (ok == 0) return Results.BadRequest(new { message = "Transición no permitida para la orden" });

        // Avanza
        const string sqlUpd = @"UPDATE ordenes SET estado_actual_id = @destId, actualizado_en = NOW() WHERE id=@orderId;";
        await db.ExecuteAsync(new CommandDefinition(sqlUpd, new { destId, orderId }, transaction: tx));

        // Historial
        const string sqlInsHist = @"
INSERT INTO historial_flujo (objeto_tipo, objeto_id, transicion_id, estado_id, usuario_id, notas, creado_en)
SELECT 'orden', @orderId, t.id, @destId, NULL, @note, NOW()
FROM transiciones t
JOIN ordenes o ON o.proceso_id = t.proceso_id
WHERE o.id=@orderId AND t.estado_desde_id <> t.estado_hasta_id AND t.estado_hasta_id = @destId
LIMIT 1;";
        await db.ExecuteAsync(new CommandDefinition(sqlInsHist, new { orderId, destId, note = body.note ?? "" }, transaction: tx));

        await tx.CommitAsync();
        return Results.Ok(new { ok = true, orderId, to = body.to });
    }
}
