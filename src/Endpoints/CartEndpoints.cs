// BaseUsuarios.Api/src/Endpoints/CartEndpoints.cs
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using MySql.Data.MySqlClient;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

namespace BaseUsuarios.Api.Endpoints
{
    public static class CartEndpoints
    {
        public static IEndpointRouteBuilder MapCartEndpoints(this IEndpointRouteBuilder app)
        {
            string GetCs(IConfiguration cfg, bool local) =>
                local
                    ? (cfg.GetConnectionString("ComprasLocal") ?? cfg.GetConnectionString("Default")!)
                    : (cfg.GetConnectionString("Default")!);

            app.MapGet("/api/local/cart", async (int usuarioId, IConfiguration cfg) =>
                await GetCartAsync(usuarioId, cfg, local: true)).WithName("GetCartLocal").WithTags("Cart","Local");

            app.MapGet("/api/cart", async (int usuarioId, IConfiguration cfg) =>
                await GetCartAsync(usuarioId, cfg, local: false)).WithName("GetCart").WithTags("Cart");

            app.MapPost("/api/local/cart/items", async (CartAddDto dto, IConfiguration cfg) =>
                await AddItemAsync(dto, cfg, local: true)).WithName("CartAddItemLocal").WithTags("Cart","Local");

            app.MapPost("/api/cart/items", async (CartAddDto dto, IConfiguration cfg) =>
                await AddItemAsync(dto, cfg, local: false)).WithName("CartAddItem").WithTags("Cart");

            app.MapPut("/api/local/cart/items/{itemId:long}", async (long itemId, UpdateQtyDto dto, IConfiguration cfg) =>
                await UpdateQtyAsync(itemId, dto, cfg, local: true)).WithName("CartUpdateItemLocal").WithTags("Cart","Local");

            app.MapPut("/api/cart/items/{itemId:long}", async (long itemId, UpdateQtyDto dto, IConfiguration cfg) =>
                await UpdateQtyAsync(itemId, dto, cfg, local: false)).WithName("CartUpdateItem").WithTags("Cart");

            app.MapDelete("/api/local/cart/items/{itemId:long}", async (long itemId, IConfiguration cfg) =>
                await DeleteItemAsync(itemId, cfg, local: true)).WithName("CartDeleteItemLocal").WithTags("Cart","Local");

            app.MapDelete("/api/cart/items/{itemId:long}", async (long itemId, IConfiguration cfg) =>
                await DeleteItemAsync(itemId, cfg, local: false)).WithName("CartDeleteItem").WithTags("Cart");

            return app;

            // ------------------------ Implementación ------------------------

            static async Task<IResult> GetCartAsync(int usuarioId, IConfiguration cfg, bool local)
            {
                if (usuarioId <= 0) return Results.BadRequest(new { message = "usuarioId inválido" });

                var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")!;
                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync();

                long? carritoId = null;
                await using (var cmd = new MySqlCommand(@"
                    SELECT id
                    FROM carritos
                    WHERE usuario_id = @u AND estado = 'abierto'
                    ORDER BY actualizado_en DESC, id DESC
                    LIMIT 1;", conn))
                {
                    cmd.Parameters.AddWithValue("@u", usuarioId);
                    var o = await cmd.ExecuteScalarAsync();
                    if (o != null) carritoId = Convert.ToInt64(o);
                }

                var resp = new CartResponse { carritoId = carritoId, items = new List<CartItemDtoOut>(), total = 0m };
                if (carritoId == null) return Results.Ok(resp);

                await using (var cmdIt = new MySqlCommand(@"
                    SELECT ci.id,
                           ci.producto_id,
                           p.nombre,
                           ci.cantidad,
                           ci.precio_unitario,
                           (ci.cantidad * ci.precio_unitario) AS subtotal
                    FROM carrito_items ci
                    LEFT JOIN productos p ON p.id = ci.producto_id
                    WHERE ci.carrito_id = @car
                    ORDER BY ci.id DESC;", conn))
                {
                    cmdIt.Parameters.AddWithValue("@car", carritoId);
                    await using var rd = await cmdIt.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
                    while (await rd.ReadAsync())
                    {
                        resp.items.Add(new CartItemDtoOut
                        {
                            id = rd.GetInt64("id"),
                            producto_id = rd.GetInt64("producto_id"),
                            nombre = rd.IsDBNull(rd.GetOrdinal("nombre")) ? null : rd.GetString("nombre"),
                            cantidad = rd.GetInt32("cantidad"),
                            precio_unitario = rd.GetDecimal("precio_unitario"),
                            subtotal = rd.GetDecimal("subtotal")
                        });
                    }
                }

                await using (var cmdTot = new MySqlCommand(@"
                    SELECT COALESCE(SUM(cantidad * precio_unitario), 0)
                    FROM carrito_items
                    WHERE carrito_id = @car;", conn))
                {
                    cmdTot.Parameters.AddWithValue("@car", carritoId);
                    var o = await cmdTot.ExecuteScalarAsync();
                    resp.total = o == null ? 0m : Convert.ToDecimal(o);
                }

                return Results.Ok(resp);
            }

            static async Task<IResult> AddItemAsync(CartAddDto dto, IConfiguration cfg, bool local)
            {
                if (dto.usuarioId <= 0 || dto.productoId <= 0 || dto.cantidad <= 0)
                    return Results.BadRequest(new { message = "Parámetros inválidos" });

                var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")!;
                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync();
                await using var tx = await conn.BeginTransactionAsync();

                try
                {
                    // 1) validar que el producto exista y esté activo
                    decimal precioBase;
                    await using (var cmdProd = new MySqlCommand(@"
                        SELECT precio_base
                        FROM productos
                        WHERE id = @p AND activo = 1
                        LIMIT 1;", conn, (MySqlTransaction)tx))
                    {
                        cmdProd.Parameters.AddWithValue("@p", dto.productoId);
                        var o = await cmdProd.ExecuteScalarAsync();
                        if (o == null) return Results.NotFound(new { message = "Producto no encontrado o inactivo" });
                        precioBase = Convert.ToDecimal(o);
                    }

                    // 2) obtener carrito ABIERTO más reciente o crearlo
                    long carritoId;
                    await using (var cmdSelCart = new MySqlCommand(@"
                        SELECT id
                        FROM carritos
                        WHERE usuario_id = @u AND estado = 'abierto'
                        ORDER BY actualizado_en DESC, id DESC
                        LIMIT 1;", conn, (MySqlTransaction)tx))
                    {
                        cmdSelCart.Parameters.AddWithValue("@u", dto.usuarioId);
                        var o = await cmdSelCart.ExecuteScalarAsync();
                        if (o == null)
                        {
                            await using var cmdInsCart = new MySqlCommand(@"
                                INSERT INTO carritos (usuario_id, estado, creado_en, actualizado_en)
                                VALUES (@u, 'abierto', NOW(), NOW());", conn, (MySqlTransaction)tx);
                            cmdInsCart.Parameters.AddWithValue("@u", dto.usuarioId);
                            await cmdInsCart.ExecuteNonQueryAsync();

                            await using var cmdId = new MySqlCommand("SELECT LAST_INSERT_ID();", conn, (MySqlTransaction)tx);
                            carritoId = Convert.ToInt64(await cmdId.ExecuteScalarAsync());
                        }
                        else carritoId = Convert.ToInt64(o);
                    }

                    // 3) UPSERT atómico (requiere UNIQUE (carrito_id,producto_id))
                    await using (var cmdUpsert = new MySqlCommand(@"
                        INSERT INTO carrito_items (carrito_id, producto_id, cantidad, precio_unitario)
                        VALUES (@car, @p, @c, @pu)
                        ON DUPLICATE KEY UPDATE cantidad = cantidad + VALUES(cantidad);", conn, (MySqlTransaction)tx))
                    {
                        cmdUpsert.Parameters.AddWithValue("@car", carritoId);
                        cmdUpsert.Parameters.AddWithValue("@p", dto.productoId);
                        cmdUpsert.Parameters.AddWithValue("@c", dto.cantidad);
                        cmdUpsert.Parameters.AddWithValue("@pu", precioBase);
                        await cmdUpsert.ExecuteNonQueryAsync();
                    }

                    // 4) toca timestamp del carrito
                    await using (var cmdTouch = new MySqlCommand(
                        "UPDATE carritos SET actualizado_en = NOW() WHERE id = @car;", conn, (MySqlTransaction)tx))
                    {
                        cmdTouch.Parameters.AddWithValue("@car", carritoId);
                        await cmdTouch.ExecuteNonQueryAsync();
                    }

                    // 5) obtener itemId para retornar
                    long itemId;
                    await using (var cmdFind = new MySqlCommand(@"
                        SELECT id FROM carrito_items
                        WHERE carrito_id = @car AND producto_id = @p
                        LIMIT 1;", conn, (MySqlTransaction)tx))
                    {
                        cmdFind.Parameters.AddWithValue("@car", carritoId);
                        cmdFind.Parameters.AddWithValue("@p", dto.productoId);
                        var o = await cmdFind.ExecuteScalarAsync();
                        itemId = Convert.ToInt64(o);
                    }

                    await tx.CommitAsync();
                    return Results.Ok(new { carritoId, itemId });
                }
                catch (MySqlException mex)
                {
                    await tx.RollbackAsync();
                    // Si el índice UNIQUE no existe, el ON DUPLICATE KEY no actuará; ayuda de diagnóstico
                    return Results.Problem($"MySQL error {mex.Number}: {mex.Message}", statusCode: 500);
                }
                catch (Exception ex)
                {
                    await tx.RollbackAsync();
                    return Results.Problem(ex.Message, statusCode: 500);
                }
            }

            static async Task<IResult> UpdateQtyAsync(long itemId, UpdateQtyDto dto, IConfiguration cfg, bool local)
            {
                if (itemId <= 0) return Results.BadRequest(new { message = "itemId inválido" });
                if (dto.cantidad < 0) return Results.BadRequest(new { message = "cantidad inválida" });

                var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")!;
                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync();

                long? carritoId = null;
                await using (var cmdGetCar = new MySqlCommand(
                    "SELECT carrito_id FROM carrito_items WHERE id=@id LIMIT 1;", conn))
                {
                    cmdGetCar.Parameters.AddWithValue("@id", itemId);
                    var o = await cmdGetCar.ExecuteScalarAsync();
                    if (o != null) carritoId = Convert.ToInt64(o);
                }

                if (dto.cantidad == 0)
                {
                    await using var del = new MySqlCommand("DELETE FROM carrito_items WHERE id=@id", conn);
                    del.Parameters.AddWithValue("@id", itemId);
                    var rows = await del.ExecuteNonQueryAsync();

                    if (rows > 0 && carritoId != null)
                    {
                        await using var touch = new MySqlCommand(
                            "UPDATE carritos SET actualizado_en = NOW() WHERE id=@car", conn);
                        touch.Parameters.AddWithValue("@car", carritoId);
                        await touch.ExecuteNonQueryAsync();
                    }
                    return rows > 0 ? Results.NoContent() : Results.NotFound();
                }
                else
                {
                    await using var upd = new MySqlCommand(@"
                        UPDATE carrito_items
                        SET cantidad=@qty
                        WHERE id=@id", conn);
                    upd.Parameters.AddWithValue("@qty", dto.cantidad);
                    upd.Parameters.AddWithValue("@id", itemId);
                    var rows = await upd.ExecuteNonQueryAsync();

                    if (rows > 0 && carritoId != null)
                    {
                        await using var touch = new MySqlCommand(
                            "UPDATE carritos SET actualizado_en = NOW() WHERE id=@car", conn);
                        touch.Parameters.AddWithValue("@car", carritoId);
                        await touch.ExecuteNonQueryAsync();
                    }
                    return rows > 0 ? Results.NoContent() : Results.NotFound();
                }
            }

            static async Task<IResult> DeleteItemAsync(long itemId, IConfiguration cfg, bool local)
            {
                if (itemId <= 0) return Results.BadRequest(new { message = "itemId inválido" });

                var cs = cfg.GetConnectionString(local ? "ComprasLocal" : "Default")!;
                await using var conn = new MySqlConnection(cs);
                await conn.OpenAsync();

                long? carritoId = null;
                await using (var cmdGetCar = new MySqlCommand(
                    "SELECT carrito_id FROM carrito_items WHERE id=@id LIMIT 1;", conn))
                {
                    cmdGetCar.Parameters.AddWithValue("@id", itemId);
                    var o = await cmdGetCar.ExecuteScalarAsync();
                    if (o != null) carritoId = Convert.ToInt64(o);
                }

                await using var del = new MySqlCommand("DELETE FROM carrito_items WHERE id=@id", conn);
                del.Parameters.AddWithValue("@id", itemId);
                var rows = await del.ExecuteNonQueryAsync();

                if (rows > 0 && carritoId != null)
                {
                    await using var touch = new MySqlCommand(
                        "UPDATE carritos SET actualizado_en = NOW() WHERE id=@car", conn);
                    touch.Parameters.AddWithValue("@car", carritoId);
                    await touch.ExecuteNonQueryAsync();
                }
                return rows > 0 ? Results.NoContent() : Results.NotFound();
            }
        }
    }

    // ------------------------- DTOs / Respuestas -------------------------

    public record CartAddDto(int usuarioId, long productoId, int cantidad);
    public record UpdateQtyDto(int cantidad);

    public class CartItemDtoOut
    {
        public long id { get; set; }
        public long producto_id { get; set; }
        public string? nombre { get; set; }
        public int cantidad { get; set; }
        public decimal precio_unitario { get; set; }
        public decimal subtotal { get; set; }
    }

    public class CartResponse
    {
        public long? carritoId { get; set; }
        public List<CartItemDtoOut> items { get; set; } = new();
        public decimal total { get; set; }
    }
}
