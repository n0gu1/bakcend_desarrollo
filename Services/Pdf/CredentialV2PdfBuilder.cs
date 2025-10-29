using System;
using System.Text.Json;
using System.Drawing;                // para Color
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using QRCoder;

namespace BaseUsuarios.Api.Services.Pdf;

/// <summary>
/// Genera un PDF "credencial" que replica tu plantilla visual,
/// usando QuestPDF (sin HTML, sin Playwright).
/// </summary>
public static class CredentialV2PdfBuilder
{
    static CredentialV2PdfBuilder()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        QuestPDF.Settings.EnableDebugging = true; // útil mientras depuramos
    }

    public static byte[] Build(
        string fullName,
        string nickname,
        string email,
        string phone,
        string? photoBase64 // Fotografía2 opcional (dataURL o base64)
    )
    {
        // QR data (igual que tu script)
        var qrData = new
        {
            nombre   = fullName,
            nickname = "@" + nickname,
            email,
            telefono = phone
        };
        var qrJson = JsonSerializer.Serialize(qrData);
        var qrPng  = BuildQrPng(qrJson, "#1e3c72", "#ffffff");

        // Foto
        var photoBytes = TryDecode(photoBase64);
        var today = DateTime.Now;

        var pdf = Document.Create(c =>
        {
            c.Page(p =>
            {
                p.Size(PageSizes.A5);
                p.Margin(20);
                p.DefaultTextStyle(t => t.FontSize(11).FontColor(Colors.BlueGrey.Darken4));

                p.Content().AlignCenter().Element(card =>
                {
                    card.Width(320)
                        .Border(1).BorderColor(Colors.Grey.Lighten3)
                        .CornerRadius(20)
                        .Background(Colors.White)
                        .Column(col =>
                        {
                            col.Spacing(10);

                            // HEADER (dejamos espacio inferior para la foto)
                            col.Item().Element(header =>
                            {
                                header
                                  .Background("#1e3c72")
                                  .PaddingTop(18).PaddingHorizontal(16).PaddingBottom(50)
                                  .Column(h =>
                                  {
                                      // icono en esquina (SIN Row/ConstantItem)
                                      h.Item().Element(e =>
                                      {
                                          e.AlignRight()
                                           .Text("🔑")
                                           .FontSize(18) // un poco menor para asegurar cabida
                                           .FontColor(Colors.White.WithAlpha(0.85f));
                                      });

                                      // logo circular con 🔑
                                      h.Item().AlignCenter().Element(e2 =>
                                      {
                                          e2
                                            .Width(80).Height(80)
                                            .Background(Colors.White)
                                            .CornerRadius(40)
                                            .AlignCenter()
                                            .AlignMiddle()
                                            .Text("🔑").FontSize(32).FontColor("#2575fc");
                                      });

                                      // puntos decorativos
                                      h.Item().AlignCenter().Text(".").FontSize(16).FontColor(Colors.White);
                                      h.Item().AlignCenter().Text(".").FontSize(16).FontColor(Colors.White);
                                      h.Item().AlignCenter().Text(".").FontSize(16).FontColor(Colors.White);
                                  });
                            });

                            // FOTO circular
                            col.Item().AlignCenter().Element(e =>
                            {
                                e
                                  .Width(100).Height(100)
                                  .Background("#f0f8ff")
                                  .CornerRadius(50)
                                  .Border(5).BorderColor(Colors.White)
                                  .Element(ph =>
                                  {
                                      if (photoBytes is { Length: > 0 })
                                          ph.Image(photoBytes).FitArea();
                                      else
                                          ph.AlignCenter().AlignMiddle().Text("👤").FontSize(42).FontColor("#2575fc");
                                  });
                            });

                            // INFO
                            col.Item().PaddingTop(6).PaddingHorizontal(20).Column(info =>
                            {
                                info.Spacing(8);

                                info.Item().AlignCenter().Text(fullName).FontSize(24).SemiBold().FontColor("#1e3c72");
                                info.Item().AlignCenter().Text("@" + nickname).FontSize(16).Italic().FontColor("#2575fc");

                                // Detalles (correo / teléfono)
                                info.Item().PaddingTop(6).Column(details =>
                                {
                                    details.Spacing(10);

                                    details.Item().Element(container =>
                                    {
                                        container
                                            .Background("#f0f8ff")
                                            .CornerRadius(10)
                                            .Padding(12)
                                            .Row(r =>
                                            {
                                                r.ConstantItem(35).AlignMiddle().Text("✉️").FontSize(18).FontColor("#2575fc");
                                                r.RelativeItem().Column(c2 =>
                                                {
                                                    c2.Item().Text("Correo Electrónico").FontSize(12).SemiBold().FontColor("#1e3c72").LineHeight(1.1f);
                                                    c2.Item().Text(email).FontSize(15).FontColor("#2a5298").LineHeight(1.25f);
                                                });
                                            });
                                    });

                                    details.Item().Element(container =>
                                    {
                                        container
                                            .Background("#f0f8ff")
                                            .CornerRadius(10)
                                            .Padding(12)
                                            .Row(r =>
                                            {
                                                r.ConstantItem(35).AlignMiddle().Text("📞").FontSize(18).FontColor("#2575fc");
                                                r.RelativeItem().Column(c3 =>
                                                {
                                                    c3.Item().Text("Número de Teléfono").FontSize(12).SemiBold().FontColor("#1e3c72").LineHeight(1.1f);
                                                    c3.Item().Text(phone).FontSize(15).FontColor("#2a5298").LineHeight(1.25f);
                                                });
                                            });
                                    });
                                });

                                // QR section
                                info.Item().PaddingTop(6).Element(qr =>
                                {
                                    qr
                                      .Background("#f0f8ff")
                                      .Border(2).BorderColor("#2575fc")
                                      .CornerRadius(15)
                                      .Padding(12)
                                      .Column(qc =>
                                      {
                                          qc.Spacing(6);

                                          qc.Item().AlignCenter()
                                            .Text("Código QR de Identificación")
                                            .FontSize(16).SemiBold().FontColor("#1e3c72");

                                          qc.Item().AlignCenter().Element(img =>
                                          {
                                              img
                                                .Background(Colors.White)
                                                .Padding(6)
                                                .CornerRadius(10)
                                                .Border(1).BorderColor(Colors.Grey.Lighten2)
                                                .Width(120).Height(120)
                                                .Image(qrPng).FitArea();
                                          });
                                      });
                                });
                            });

                            // FOOTER
                            col.Item().Background("#1e3c72")
                               .Padding(12)
                               .AlignCenter()
                               .Text("Presentar esta credencial para acceder a tu cuenta")
                               .FontColor(Colors.White).FontSize(12);

                            // fecha pequeña (opcional)
                            col.Item().AlignRight().PaddingRight(10)
                               .Text(today.ToString("dd/MM/yyyy HH:mm"))
                               .FontSize(9).FontColor(Colors.Grey.Darken2);
                        });
                });
            });
        }).GeneratePdf();

        return pdf;
    }

    private static byte[] BuildQrPng(string text, string hexDark, string hexLight)
    {
        using var gen  = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.H);
        using var png  = new PngByteQRCode(data);

        var dark  = ColorTranslator.FromHtml(hexDark);
        var light = ColorTranslator.FromHtml(hexLight);

        return png.GetGraphic(6, dark, light, true);
    }

    /// <summary>Soporta dataURL (data:image/...;base64,xxx) o base64 puro.</summary>
    private static byte[]? TryDecode(string? dataUrl)
    {
        if (string.IsNullOrWhiteSpace(dataUrl)) return null;
        try
        {
            var parts = dataUrl.Split(',', 2);
            var b64 = parts.Length == 2 ? parts[1] : dataUrl;
            return Convert.FromBase64String(b64);
        }
        catch { return null; }
    }
}
