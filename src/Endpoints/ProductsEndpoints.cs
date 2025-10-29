using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using System.Collections.Generic;

namespace BaseUsuarios.Api.Endpoints
{
    public static class ProductsEndpoints
    {
        public static IEndpointRouteBuilder MapProductsEndpoints(this IEndpointRouteBuilder app)
        {
            var cfg = app.ServiceProvider.GetRequiredService<IConfiguration>();
            string GetCs() => cfg.GetConnectionString("Default")!;
            string GetCsLocal() => cfg.GetConnectionString("ComprasLocal") ?? GetCs();

            // ---- REMOTO: /api/products (usa ConnectionStrings:Default)
            app.MapGet("/api/products", async () =>
            {
                var list = new List<object>();
                await using var conn = new MySqlConnection(GetCs());
                await conn.OpenAsync();

                const string sql = @"
                    SELECT id, nombre, precio_base
                    FROM productos
                    WHERE activo = 1
                    ORDER BY id DESC";

                await using var cmd = new MySqlCommand(sql, conn);
                await using var rd = await cmd.ExecuteReaderAsync();

                var ordId = rd.GetOrdinal("id");
                var ordNombre = rd.GetOrdinal("nombre");
                var ordPrecio = rd.GetOrdinal("precio_base");

                while (await rd.ReadAsync())
                {
                    list.Add(new
                    {
                        id = rd.GetInt64(ordId),
                        nombre = rd.IsDBNull(ordNombre) ? null : rd.GetString(ordNombre),
                        precio_base = rd.GetDecimal(ordPrecio)
                    });
                }

                return Results.Ok(list);
            })
            .WithName("ProductsList")
            .WithTags("Products");

            // ---- LOCAL: /api/products/local (usa ConnectionStrings:ComprasLocal)
            app.MapGet("/api/products/local", async () =>
            {
                var list = new List<object>();
                await using var conn = new MySqlConnection(GetCsLocal());
                await conn.OpenAsync();

                const string sql = @"
                    SELECT id, nombre, precio_base
                    FROM productos
                    WHERE activo = 1
                    ORDER BY id DESC";

                await using var cmd = new MySqlCommand(sql, conn);
                await using var rd = await cmd.ExecuteReaderAsync();

                var ordId = rd.GetOrdinal("id");
                var ordNombre = rd.GetOrdinal("nombre");
                var ordPrecio = rd.GetOrdinal("precio_base");

                while (await rd.ReadAsync())
                {
                    list.Add(new
                    {
                        id = rd.GetInt64(ordId),
                        nombre = rd.IsDBNull(ordNombre) ? null : rd.GetString(ordNombre),
                        precio_base = rd.GetDecimal(ordPrecio)
                    });
                }

                return Results.Ok(list);
            })
            .WithName("ProductsListLocal")
            .WithTags("Products", "Local");

            return app;
        }
    }
}
