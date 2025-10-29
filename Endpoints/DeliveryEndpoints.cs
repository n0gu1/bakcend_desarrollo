using System.Data;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;

namespace BaseUsuarios.Api.Endpoints;

public static class DeliveryEndpoints
{
    static IDbConnection OpenLocal(HttpContext ctx)
    {
        var cfg = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var cs  = cfg.GetConnectionString("ComprasLocal")!;
        var db  = new MySqlConnection(cs);
        db.Open();
        return db;
    }

    public static void MapDeliveryEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/Local");

        g.MapGet("/areas", async (HttpContext ctx) =>
        {
            using var db = OpenLocal(ctx);
            var rows = await db.QueryAsync("SELECT id, nombre FROM areas_entrega ORDER BY nombre;");
            return Results.Ok(rows);
        });

        g.MapGet("/puntos", async (HttpContext ctx, long areaId) =>
        {
            using var db = OpenLocal(ctx);
            var rows = await db.QueryAsync(
                "SELECT id, nombre, lat, lng FROM puntos_entrega WHERE area_id=@areaId ORDER BY nombre;",
                new { areaId });
            return Results.Ok(rows);
        });
    }
}
