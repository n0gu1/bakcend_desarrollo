// src/Controllers/DashboardController.cs
using System.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Controllers.Dashboard;

[ApiController]
[Route("api/[controller]")]
public class DashboardController : ControllerBase
{
    private readonly IConfiguration _cfg;
    public DashboardController(IConfiguration cfg) => _cfg = cfg;

    public record DashboardResp(
        Kpis kpis,
        IEnumerable<SerieP> series,
        IEnumerable<ByEstadoRow> byEstado,
        IEnumerable<ByTipoRow> byTipoProducto,
        IEnumerable<TopProductoRow> topProductos
    );

    public record Kpis(decimal totalVentas, long totalOrdenes, decimal avgTicket);
    public record SerieP(string bucket, decimal total, long ordenes);
    public record ByEstadoRow(string estado, long ordenes, decimal total);
    public record ByTipoRow(string tipo, long ordenes, decimal total);
    public record TopProductoRow(string nombre, long unidades, decimal total);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? granularity = "day")
    {
        // Defaults: últimos 7 días, por día
        DateTime toDt = ParseDateOrToday(to, DateTime.UtcNow.Date);
        DateTime fromDt = ParseDateOrToday(from, toDt.AddDays(-7));
        string g = (granularity ?? "day").Trim().ToLowerInvariant();
        if (g is not ("day" or "week" or "total")) g = "day";

        // Preferir 'ComprasLocal' y caer a otras si existieran
        var cs =
            _cfg.GetConnectionString("ComprasLocal") ??
            _cfg.GetConnectionString("DefaultConnection") ??
            _cfg.GetConnectionString("Default") ??
            _cfg["ConnectionStrings:ComprasLocal"] ??
            _cfg["ConnectionStrings:DefaultConnection"] ??
            _cfg["ConnectionStrings:Default"];

        if (string.IsNullOrWhiteSpace(cs))
            return Problem("Connection string 'ComprasLocal' no configurada.");

        await using var cn = new MySqlConnection(cs);
        await cn.OpenAsync();

        var p = new DynamicParameters();
        p.Add("@from", fromDt);
        p.Add("@to",   toDt.AddDays(1).AddTicks(-1)); // fin de día inclusivo

        // ===== KPIs
        const string sqlKpis = @"
            SELECT
              COALESCE(SUM(o.total),0) AS totalVentas,
              COUNT(*)                  AS totalOrdenes
            FROM ordenes o
            WHERE o.creado_en BETWEEN @from AND @to;";
        var k = await cn.QuerySingleAsync(sqlKpis, p);
        decimal totalVentas = (decimal)(k.totalVentas ?? 0m);
        long totalOrdenes   = (long)(k.totalOrdenes ?? 0L);
        decimal avgTicket   = totalOrdenes > 0 ? (totalVentas / totalOrdenes) : 0m;
        var kpis = new Kpis(totalVentas, totalOrdenes, avgTicket);

        // ===== Series (día/semana/total)
        // Proyectamos el bucket como string (CAST) y AGRUPAMOS por la MISMA expresión para cumplir ONLY_FULL_GROUP_BY
        string selectBucket = g switch
        {
            "day"  => "CAST(DATE(o.creado_en) AS CHAR)",
            "week" => "CAST(CONCAT(YEAR(o.creado_en), '-W', LPAD(WEEK(o.creado_en, 3),2,'0')) AS CHAR)",
            _      => "'TOTAL'"
        };

        // Para ordenar cronológicamente, usamos un "ordenador" agregable por bucket
        string orderKey = g switch
        {
            "day"  => "MIN(DATE(o.creado_en))",
            "week" => "MIN(STR_TO_DATE(CONCAT(YEAR(o.creado_en), ' ', WEEK(o.creado_en,3), ' Monday'), '%X %V %W'))",
            _      => "MIN(o.creado_en)"
        };

        string sqlSeries = $@"
            SELECT
              {selectBucket} AS bucket,
              COALESCE(SUM(o.total),0) AS total,
              COUNT(*) AS ordenes
            FROM ordenes o
            WHERE o.creado_en BETWEEN @from AND @to
            GROUP BY {selectBucket}
            ORDER BY {orderKey} ASC;";
        var series = (await cn.QueryAsync<SerieP>(sqlSeries, p)).ToList();

        // ===== Por estado
        const string sqlByEstado = @"
            SELECT
              COALESCE(e.nombre, '—') AS estado,
              COUNT(*) AS ordenes,
              COALESCE(SUM(o.total),0) AS total
            FROM ordenes o
            LEFT JOIN estados e ON e.id = o.estado_actual_id
            WHERE o.creado_en BETWEEN @from AND @to
            GROUP BY e.nombre
            ORDER BY ordenes DESC;";
        var byEstado = await cn.QueryAsync<ByEstadoRow>(sqlByEstado, p);

        // ===== Por “tipo” (usamos productos.nombre como tipo)
        const string sqlByTipo = @"
            SELECT
              COALESCE(p.nombre, CONCAT('Producto #', oi.producto_id)) AS tipo,
              COUNT(DISTINCT oi.orden_id) AS ordenes,
              COALESCE(SUM(oi.cantidad * oi.precio_unitario), 0) AS total
            FROM orden_items oi
            INNER JOIN ordenes o ON o.id = oi.orden_id
            LEFT JOIN productos p ON p.id = oi.producto_id
            WHERE o.creado_en BETWEEN @from AND @to
            GROUP BY p.nombre, oi.producto_id
            ORDER BY total DESC;";
        var byTipo = await cn.QueryAsync<ByTipoRow>(sqlByTipo, p);

        // ===== Top productos  (⚠️ casteo de unidades a entero para Dapper -> long)
        const string sqlTop = @"
            SELECT
              COALESCE(p.nombre, CONCAT('Producto #', oi.producto_id)) AS nombre,
              CAST(COALESCE(SUM(oi.cantidad),0) AS SIGNED) AS unidades,
              COALESCE(SUM(oi.cantidad * oi.precio_unitario),0) AS total
            FROM orden_items oi
            INNER JOIN ordenes o ON o.id = oi.orden_id
            LEFT JOIN productos p ON p.id = oi.producto_id
            WHERE o.creado_en BETWEEN @from AND @to
            GROUP BY p.nombre, oi.producto_id
            ORDER BY unidades DESC, total DESC
            LIMIT 10;";
        var top = await cn.QueryAsync<TopProductoRow>(sqlTop, p);

        var resp = new DashboardResp(kpis, series, byEstado, byTipo, top);
        return Ok(resp);
    }

    private static DateTime ParseDateOrToday(string? s, DateTime fallback)
        => DateTime.TryParse(s, out var d) ? d.Date : fallback.Date;
}
