using System;
using System.Threading;
using System.Threading.Tasks;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace BaseUsuarios.Api.Services.Pdf;

public sealed class RegistrationPdfService : IRegistrationPdfService
{
    // ⛳ Se ejecuta una sola vez por AppDomain: fija la licencia Community
    static RegistrationPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public Task<byte[]> BuildReceiptAsync(string nickname, string phone, string email, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow;

        var bytes = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(36);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(t => t.FontSize(12));

                page.Header().Element(h =>
                {
                    h.Row(r =>
                    {
                        r.AutoItem().Text("Comprobante de Registro").FontSize(20).SemiBold();
                        r.RelativeItem().AlignRight().Text(today.ToString("yyyy-MM-dd HH:mm 'UTC'"));
                    });
                });

                page.Content().PaddingVertical(10).Column(col =>
                {
                    col.Item().Text("Datos del usuario").FontSize(14).SemiBold().Underline();

                    col.Item().PaddingTop(8).Table(t =>
                    {
                        t.ColumnsDefinition(c =>
                        {
                            c.ConstantColumn(120);
                            c.RelativeColumn();
                        });

                        void Row(string label, string val)
                        {
                            t.Cell().Padding(4).Text(label).SemiBold();
                            t.Cell().Padding(4).Text(val);
                        }

                        Row("Nickname", nickname);
                        Row("Teléfono", phone);
                        Row("Email", email);
                    });

                    col.Item().PaddingTop(14)
                        .Text("Gracias por registrarte.")
                        .Italic()
                        .FontColor(Colors.Grey.Darken2);
                });

                page.Footer().AlignRight().Text(txt =>
                {
                    txt.Span("Proyecto Final — ")
                       .FontSize(10)
                       .FontColor(Colors.Grey.Darken1);

                    txt.Span("Comprobante de registro")
                       .Italic()
                       .FontSize(10)
                       .FontColor(Colors.Grey.Darken1);
                });
            });
        }).GeneratePdf();

        return Task.FromResult(bytes);
    }
}
