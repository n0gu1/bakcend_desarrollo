// BaseUsuarios.Api/Services/PdfHtml/OrderReceiptHtmlBuilder.cs
using System;
using System.Globalization;
using System.Net;
using System.Text;
using QRCoder;

namespace BaseUsuarios.Api.Services.PdfHtml
{
    public static class OrderReceiptHtmlBuilder
    {
        public sealed class OrderItem
        {
            public string Nombre { get; }
            public int Cantidad { get; }
            public decimal PrecioUnitario { get; }

            public OrderItem(string nombre, int cantidad, decimal precioUnitario)
            {
                Nombre = nombre ?? string.Empty;
                Cantidad = cantidad;
                PrecioUnitario = precioUnitario;
            }
        }

        // Formateo de moneda compatible con InvariantGlobalization
        private static string Q(decimal value)
            => "Q " + value.ToString("N2", CultureInfo.InvariantCulture);

        public static string Build(
            string folio,
            string email,
            string nickname,
            decimal total,
            System.Collections.Generic.IEnumerable<OrderItem> items,
            string trackingUrlBase = "https://mi-tienda.ejemplo/tracking")
        {
            string esc(string s) => WebUtility.HtmlEncode(s ?? string.Empty);

            string trackingUrl = (trackingUrlBase ?? "https://mi-tienda.ejemplo/tracking").TrimEnd('/') + "/" + folio;
            string qrBase64 = BuildQrPngBase64(trackingUrl);

            var rowsSb = new StringBuilder();
            if (items != null)
            {
                foreach (var it in items)
                {
                    decimal sub = it.Cantidad * it.PrecioUnitario;
                    rowsSb.AppendLine(
                        "<tr>" +
                        "<td>" + esc(it.Nombre) + "</td>" +
                        "<td style=\"text-align:center\">" + it.Cantidad + "</td>" +
                        "<td style=\"text-align:right\">" + Q(it.PrecioUnitario) + "</td>" +
                        "<td style=\"text-align:right\">" + Q(sub) + "</td>" +
                        "</tr>"
                    );
                }
            }

            string rows     = rowsSb.ToString();
            string totalStr = Q(total);

            var htmlSb = new StringBuilder();
            htmlSb.Append(@"<!doctype html><html lang=""es""><head>");
            htmlSb.Append(@"<meta charset=""utf-8""><title>Constancia de compra</title>");
            htmlSb.Append(@"<style>
 body{font-family:Arial,Helvetica,sans-serif;color:#222}
 .wrap{max-width:760px;margin:24px auto;border:1px solid #eee;border-radius:12px;overflow:hidden}
 .header{background:#121826;color:#fff;padding:16px 20px;font-size:18px}
 .content{padding:20px}
 table{width:100%;border-collapse:collapse;margin-top:10px}
 th,td{border-bottom:1px solid #eee;padding:8px}
 .right{text-align:right}
 .qr{text-align:center;margin-top:16px}
 .foot{background:#f7f7f8;padding:12px 20px;color:#444}
</style>");
            htmlSb.Append("</head><body>");
            htmlSb.Append(@"<div class=""wrap"">");
            htmlSb.Append(@"<div class=""header"">Constancia de compra · Folio " + esc(folio) + "</div>");
            htmlSb.Append(@"<div class=""content"">");
            htmlSb.Append("<p>Hola <b>" + esc(nickname) + "</b>,</p>");
            htmlSb.Append("<p>Adjuntamos tu constancia de compra. Presenta el siguiente código en el punto de entrega.</p>");
            htmlSb.Append("<table><thead><tr><th>Producto</th><th style=\"text-align:center\">Cant.</th><th class=\"right\">P. Unit.</th><th class=\"right\">Subtotal</th></tr></thead>");
            htmlSb.Append("<tbody>" + rows + "</tbody>");
            htmlSb.Append("<tfoot><tr><td colspan=\"3\" class=\"right\"><b>Total</b></td><td class=\"right\"><b>" + totalStr + "</b></td></tr></tfoot>");
            htmlSb.Append("</table>");
            htmlSb.Append("<div class=\"qr\">");
            htmlSb.Append("  <div>Seguimiento: <a href=\"" + esc(trackingUrl) + "\">" + esc(trackingUrl) + "</a></div>");
            htmlSb.Append("  <img width=\"140\" height=\"140\" src=\"data:image/png;base64," + qrBase64 + "\" alt=\"QR\" />");
            htmlSb.Append("</div></div><div class=\"foot\">Gracias por tu compra. Este documento facilita la entrega.</div></div>");
            htmlSb.Append("</body></html>");

            return htmlSb.ToString();
        }

        private static string BuildQrPngBase64(string text)
        {
            using (var gen = new QRCodeGenerator())
            using (var data = gen.CreateQrCode(text ?? string.Empty, QRCodeGenerator.ECCLevel.H))
            using (var png = new PngByteQRCode(data))
            {
                byte[] bytes = png.GetGraphic(6);
                return Convert.ToBase64String(bytes);
            }
        }
    }
}
