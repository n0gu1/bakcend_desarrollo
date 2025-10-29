// BaseUsuarios.Api/Controllers/Orders/OrderReceiptController.cs
using System.ComponentModel.DataAnnotations;
using System.Net;
using BaseUsuarios.Api.Services.Email;     // IEmailSender (ya lo tienes en el proyecto)
using BaseUsuarios.Api.Services.PdfHtml;  // OrderReceiptHtmlBuilder
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BaseUsuarios.Api.Controllers.Orders;

[ApiController]
[Route("api/orders")]
public sealed class OrderReceiptController : ControllerBase
{
    public sealed class OrderItemDto
    {
        [Required] public string Nombre { get; set; } = "";
        [Range(1, int.MaxValue)] public int Cantidad { get; set; }
        [Range(0, 9999999)] public decimal PrecioUnitario { get; set; }
    }

    public sealed class OrderReceiptDto
    {
        [Required, EmailAddress] public string Email { get; set; } = "";
        [Required] public string Nickname { get; set; } = "";
        [Required] public string Folio { get; set; } = "";
        [Range(0, 999999999)] public decimal Total { get; set; }
        [MinLength(1)] public List<OrderItemDto> Items { get; set; } = new();
    }

    private readonly IEmailSender _email;
    private readonly IHtmlPdfService _htmlPdf; // ya existe en tu proyecto
    private readonly IConfiguration _cfg;
    private readonly ILogger<OrderReceiptController> _log;

    public OrderReceiptController(IEmailSender email, IHtmlPdfService htmlPdf, IConfiguration cfg, ILogger<OrderReceiptController> log)
    {
        _email = email; _htmlPdf = htmlPdf; _cfg = cfg; _log = log;
    }

    [HttpPost("receipt-email")]
    public async Task<IActionResult> SendReceiptEmail([FromBody] OrderReceiptDto dto, CancellationToken ct)
    {
        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        var trackingBase = _cfg["App:TrackingUrlBase"] ?? "https://mi-tienda.ejemplo/tracking";

        // 1) HTML con QR
        var html = OrderReceiptHtmlBuilder.Build(
            folio: dto.Folio,
            email: dto.Email,
            nickname: dto.Nickname,
            total: dto.Total,
            items: dto.Items.Select(i => new OrderReceiptHtmlBuilder.OrderItem(i.Nombre, i.Cantidad, i.PrecioUnitario)),
            trackingUrlBase: trackingBase
        );

        // 2) HTML → PDF
        var pdfBytes = _htmlPdf.FromHtml(html);

        // 3) Enviar correo con adjunto PDF
        var fileName = $"Constancia_{dto.Folio}.pdf";
        var body = $@"<p>Hola <b>{WebUtility.HtmlEncode(dto.Nickname)}</b>,</p>
                      <p>Adjuntamos tu constancia de compra.</p>
                      <p>Gracias por tu preferencia.</p>";

        await _email.SendAsync(
            toEmail: dto.Email,
            subject: $"Constancia de compra · {dto.Folio}",
            htmlBody: body,
            attachment: (fileName, pdfBytes, "application/pdf"),
            ct: ct
        );

        return Ok(new { success = true, message = "Constancia enviada al correo." });
    }
}
