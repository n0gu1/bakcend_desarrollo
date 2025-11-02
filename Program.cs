// BaseUsuarios.Api/Program.cs
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using MySql.Data.MySqlClient;
using Api.Endpoints;

using BaseUsuarios.Api.Endpoints;
using BaseUsuarios.Api.Services.PdfHtml;  // IHtmlPdfService, ChromiumHtmlPdfService
using BaseUsuarios.Api.Services.Pdf;     // IRegistrationPdfService, RegistrationPdfService
using BaseUsuarios.Api.Services.Email;   // IEmailSender, MailKitEmailSender

// 🔵 Nuevo: para respetar Scheme/Host detrás de Azure
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.FileProviders;

namespace BaseUsuarios.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // MySQL: usa "ComprasLocal" y si no, "Default"
            builder.Services.AddScoped<MySqlConnection>(sp =>
            {
                var cfg = sp.GetRequiredService<IConfiguration>();
                var cs = cfg.GetConnectionString("ComprasLocal")
                         ?? cfg.GetConnectionString("Default")
                         ?? throw new InvalidOperationException("No hay ConnectionString 'ComprasLocal' ni 'Default'.");
                return new MySqlConnection(cs);
            });

            builder.Services.AddControllers();

            // Swagger / OpenAPI
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo
                {
                    Title = "BaseUsuarios API",
                    Version = "v1",
                    Description = "API del Proyecto Final (usuarios, carrito, pedidos, PDF, etc.)"
                });
            });

            // SMTP options
            builder.Services.Configure<BaseUsuarios.Api.Config.SmtpOptions>(
                builder.Configuration.GetSection("Smtp"));

            // PDF & Email
            builder.Services.AddSingleton<IRegistrationPdfService, RegistrationPdfService>();
            builder.Services.AddSingleton<IEmailSender, MailKitEmailSender>();

            // HTML→PDF con Chromium
            builder.Services.AddSingleton<IHtmlPdfService, ChromiumHtmlPdfService>();

            // CORS (ajusta orígenes si usas otro front)
            builder.Services.AddCors(o =>
            {
                o.AddPolicy("dev", p => p
                    .WithOrigins(
                        "http://localhost:5173",
                        "http://127.0.0.1:5173",
                        "https://llaveros-umg-2.netlify.app" // agrega aquí otros dominios del front si cambian
                    )
                    .AllowAnyHeader()
                    .AllowAnyMethod()
                );
            });

            // Config FaceSeg con defaults
            var faceCfg = builder.Configuration.GetSection("FaceSeg").Get<FaceSegConfig>()
                          ?? new FaceSegConfig
                          {
                              Enabled = true,
                              Endpoint = "http://www.server.daossystem.pro:3406/api/Rostro/Segmentar",
                              TimeoutSeconds = 60
                          };
            builder.Services.AddSingleton(faceCfg);

            // HttpClient FaceSeg
            builder.Services.AddHttpClient("faceVerify", c =>
            {
                c.BaseAddress = new Uri("http://www.server.daossystem.pro:3406/");
                c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                c.Timeout = TimeSpan.FromSeconds(7);
            });

            builder.Services.AddMemoryCache();

            var app = builder.Build();

            // Crear wwwroot/uploads (después de Build)
            var webRoot = app.Environment.WebRootPath ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
            Directory.CreateDirectory(Path.Combine(webRoot, "uploads"));

            // Swagger en Dev y opcional en Prod con ENABLE_SWAGGER=true
            if (app.Environment.IsDevelopment() ||
                string.Equals(Environment.GetEnvironmentVariable("ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase))
            {
                app.UseSwagger();
                app.UseSwaggerUI(c =>
                {
                    c.SwaggerEndpoint("/swagger/v1/swagger.json", "BaseUsuarios API v1");
                    c.RoutePrefix = "swagger";
                });
            }

            // 🔵 Respetar X-Forwarded-* de Azure para Scheme/Host correctos
            var fwd = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor
                                 | ForwardedHeaders.XForwardedProto
                                 | ForwardedHeaders.XForwardedHost
            };
            fwd.KnownNetworks.Clear();
            fwd.KnownProxies.Clear();
            app.UseForwardedHeaders(fwd);

            // CORS antes de estáticos/endpoints
            app.UseCors("dev");

            // Estáticos (CORS para <canvas> + cache)
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = ctx =>
                {
                    ctx.Context.Response.Headers["Access-Control-Allow-Origin"] = "*"; // si usas cookies, usa tu dominio en vez de "*"
                    ctx.Context.Response.Headers["Cache-Control"] = "public,max-age=31536000,immutable";
                }
            });

            // Endpoints propios
            app.MapProductsEndpoints();
            app.MapCartEndpoints();
            app.MapPersonalizationEndpoints();
            app.MapPersonalizationUploadsEndpoints();
            app.MapUserPhotoEndpoints();
            app.MapOrdersEndpoints();
            app.MapDeliveryEndpoints();
            app.MapOrderHistoryEndpoints();
            app.MapOperadorEndpoints();
            app.MapOrdenesImagesEndpoints();  // <<--- NUEVO
            app.MapShopImagesDirectEndpoints();   // <= NUEVO
            app.MapOrderImagesDirectEndpoints();
            app.MapSimpleB64Endpoints();
            app.MapSupervisorEndpoints();
            app.MapAuthLoginWithRoleEndpoints();




            // 🔵 Mantén la extensión — aquí se mapean /images-b64
            app.MapOrderImagesB64Endpoints();

            // Health
            app.MapGet("/api/ping", () => Results.Ok(new { ok = true, msg = "pong" }));

            // Preflight manual (FaceSeg)
            app.MapMethods("/api/util/segmentar-rostro", new[] { "OPTIONS" }, (HttpContext ctx) =>
            {
                ctx.Response.Headers["Access-Control-Allow-Origin"] = ctx.Request.Headers["Origin"];
                ctx.Response.Headers["Vary"] = "Origin";
                ctx.Response.Headers["Access-Control-Allow-Headers"] = "content-type";
                ctx.Response.Headers["Access-Control-Allow-Methods"] = "POST, OPTIONS";
                return Results.Ok();
            });

            // Proxy normal (FaceSeg)
            app.MapPost("/api/util/segmentar-rostro", async (
                UtilSegmentRequest req,
                IHttpClientFactory http,
                FaceSegConfig cfg,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(req.PhotoBase64))
                    return Results.Ok(new { ok = false, segmentado = false, dataUrl = (string?)null, message = "Sin foto" });

                if (!cfg.Enabled || string.IsNullOrWhiteSpace(cfg.Endpoint))
                    return Results.Ok(new { ok = false, segmentado = false, dataUrl = (string?)null, message = "FaceSeg no configurado" });

                var raw = FaceSegHelper.StripDataUrlPrefix(req.PhotoBase64);
                var client = http.CreateClient("faceVerify");

                var (ok, seg, rostroB64, err) = await FaceSegHelper.SegmentFaceLikePythonAsync(client, cfg.Endpoint!, raw, ct);

                if (ok && seg && !string.IsNullOrWhiteSpace(rostroB64))
                    return Results.Ok(new { ok = true, segmentado = true, dataUrl = $"data:image/png;base64,{rostroB64}" });

                if (ok && !seg)
                    return Results.Ok(new { ok = true, segmentado = false, dataUrl = (string?)null, message = "No se detectó rostro" });

                return Results.Ok(new { ok = false, segmentado = false, dataUrl = (string?)null, message = err ?? "Error FaceSeg" });
            });

            // Proxy verbose (debug)
            app.MapPost("/api/util/segmentar-rostro-verbose", async (
                UtilSegmentRequest req,
                IHttpClientFactory http,
                FaceSegConfig cfg,
                CancellationToken ct) =>
            {
                string Strip(string s)
                {
                    if (string.IsNullOrWhiteSpace(s)) return s;
                    if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        var i = s.IndexOf(',');
                        if (i >= 0 && i + 1 < s.Length) return s[(i + 1)..];
                    }
                    return s;
                }

                var raw = Strip(req.PhotoBase64 ?? "");
                var client = http.CreateClient("faceVerify");

                var jsonA = JsonSerializer.Serialize(new { RostroA = raw, RostroB = raw }, new JsonSerializerOptions { PropertyNamingPolicy = null });
                using var rA = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint!) { Content = new StringContent(jsonA, Encoding.UTF8, "application/json") };
                rA.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var resA = await client.SendAsync(rA, HttpCompletionOption.ResponseHeadersRead, ct);
                var bodyA = await resA.Content.ReadAsStringAsync(ct);

                var jsonB = JsonSerializer.Serialize(new { Rostro = raw }, new JsonSerializerOptions { PropertyNamingPolicy = null });
                using var rB = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint!) { Content = new StringContent(jsonB, Encoding.UTF8, "application/json") };
                rB.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var resB = await client.SendAsync(rB, HttpCompletionOption.ResponseHeadersRead, ct);
                var bodyB = await resB.Content.ReadAsStringAsync(ct);

                FaceSegResponse? dataA = null, dataB = null;
                try { dataA = JsonSerializer.Deserialize<FaceSegResponse>(bodyA, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch { }
                try { dataB = JsonSerializer.Deserialize<FaceSegResponse>(bodyB, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); } catch { }

                string? rostro = null; bool seg = false;
                if (resA.IsSuccessStatusCode && dataA is { Resultado: true, Segmentado: true, Rostro: not null }) { rostro = dataA.Rostro; seg = true; }
                else if (resB.IsSuccessStatusCode && dataB is { Resultado: true, Segmentado: true, Rostro: not null }) { rostro = dataB.Rostro; seg = true; }

                return Results.Ok(new
                {
                    payloadA = new { status = (int)resA.StatusCode, ok = resA.IsSuccessStatusCode, bodyFirst200 = bodyA[..Math.Min(bodyA.Length, 200)] },
                    payloadB = new { status = (int)resB.StatusCode, ok = resB.IsSuccessStatusCode, bodyFirst200 = bodyB[..Math.Min(bodyB.Length, 200)] },
                    result = new { segmentado = seg, rostroLen = rostro?.Length ?? 0 }
                });
            });

            app.MapAuthRegisterEndpoints();
            app.MapControllers();

            app.Run();
        }
    }

    // ===== DTOs / Config / Helper =====
    public record UtilSegmentRequest([property: JsonPropertyName("photoBase64")] string PhotoBase64);

    public record FaceSegResponse(
        [property: JsonPropertyName("resultado")] bool Resultado,
        [property: JsonPropertyName("segmentado")] bool Segmentado,
        [property: JsonPropertyName("rostro")] string? Rostro
    );

    public class FaceSegConfig
    {
        public bool Enabled { get; set; } = true;
        public string? Endpoint { get; set; }
        public int TimeoutSeconds { get; set; } = 60;
    }

    public static class FaceSegHelper
    {
        private static readonly JsonSerializerOptions UppercaseOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        private static readonly JsonSerializerOptions CaseInsensitiveOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        public static string StripDataUrlPrefix(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var i = s.IndexOf(',');
                if (i >= 0 && i + 1 < s.Length) return s[(i + 1)..];
            }
            return s;
        }

        public static async Task<(bool ok, bool seg, string? base64, string? err)> SegmentFaceLikePythonAsync(
            HttpClient client, string endpoint, string base64, CancellationToken ct)
        {
            var json = JsonSerializer.Serialize(new { RostroA = base64, RostroB = base64 }, UppercaseOptions);
            using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
            {
                var fbJson = JsonSerializer.Serialize(new { Rostro = base64 }, UppercaseOptions);
                using var fbReq = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(fbJson, Encoding.UTF8, "application/json")
                };
                fbReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                using var fbResp = await client.SendAsync(fbReq, HttpCompletionOption.ResponseHeadersRead, ct);
                var fbBody = await fbResp.Content.ReadAsStringAsync(ct);
                if (!fbResp.IsSuccessStatusCode)
                    return (false, false, null, $"Proveedor {(int)fbResp.StatusCode}");

                try
                {
                    var dataFb = JsonSerializer.Deserialize<FaceSegResponse>(fbBody, CaseInsensitiveOptions);
                    if (dataFb is { Resultado: true, Segmentado: true, Rostro: not null })
                        return (true, true, dataFb.Rostro, null);
                    return (true, false, null, null);
                }
                catch { return (false, false, null, "JSON inválido (fallback)"); }
            }

            try
            {
                var data = JsonSerializer.Deserialize<FaceSegResponse>(body, CaseInsensitiveOptions);
                if (data is { Resultado: true, Segmentado: true, Rostro: not null })
                    return (true, true, data.Rostro, null);
                return (true, false, null, null);
            }
            catch { return (false, false, null, "JSON inválido"); }
        }
    }
}
