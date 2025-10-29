using System;
using System.Net;
using System.Text.Json;
using QRCoder;

namespace BaseUsuarios.Api.Services.PdfHtml;

public static class CredentialHtmlBuilder
{
    public static string Build(
        string fullName,
        string nickname,
        string email,
        string phone,
        string? photoDataUrl // data:image/...;base64,xxx  ó base64 puro
    )
    {
        var qrPayload = new
        {
            nombre   = fullName,
            nickname = "@" + nickname,
            email,
            telefono = phone
        };
        var qrJson  = JsonSerializer.Serialize(qrPayload);
        var qrBase64 = BuildQrPngBase64(qrJson); // PNG base64

        // Si la foto viene en base64 “puro”, la convertimos a dataURL JPEG
        if (!string.IsNullOrWhiteSpace(photoDataUrl) && !photoDataUrl!.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            photoDataUrl = "data:image/jpeg;base64," + photoDataUrl;

        string esc(string s) => WebUtility.HtmlEncode(s);

        // ---- TU PLANTILLA (sin dependencias externas) ----
        // Cambié <i class="fas ..."> por emojis para evitar FontAwesome.
        // El QR va en <img src="data:image/png;base64,...">
        // La foto (si viene) se ve; si no, mostramos el icono 👤.

        return $@"
<!DOCTYPE html>
<html lang=""es"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Credencial Llaveros</title>
    <style>
        * {{ margin:0; padding:0; box-sizing:border-box; font-family:'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; }}
        h1 {{ color:#1e3c72; }}
        body {{
            display:flex; justify-content:center; align-items:center; min-height:100vh;
            background:linear-gradient(135deg,#1e3c72 0%,#2a5298 100%); padding:20px;
        }}
        .credential {{
            width:320px; background:#fff; border-radius:20px;
            box-shadow:0 15px 30px rgba(0,0,0,0.3); overflow:hidden; position:relative;
        }}
        .header {{
            background:linear-gradient(90deg,#2575fc 0%,#1e3c72 100%);
            padding:25px 20px; text-align:center; color:#fff; position:relative;
        }}
        .logo {{
            width:80px; height:80px; background:#fff; border-radius:50%;
            margin:0 auto 15px; display:flex; justify-content:center; align-items:center;
            font-size:32px; color:#2575fc; box-shadow:0 5px 15px rgba(0,0,0,0.2);
        }}
        .keychain-icon {{ position:absolute; top:20px; right:20px; font-size:24px; opacity:.85; }}
        .photo-container {{
            width:100px; height:100px; border-radius:50%; overflow:hidden;
            margin:-50px auto 15px; border:5px solid #fff; box-shadow:0 5px 15px rgba(0,0,0,0.1);
            position:relative; z-index:2; background:#f0f8ff; display:flex; justify-content:center; align-items:center; color:#2575fc; font-size:40px;
        }}
        .photo-container img {{ width:100%; height:100%; object-fit:cover; }}
        .info {{ padding:50px 25px 25px; }}
        .name {{ font-size:24px; font-weight:600; text-align:center; margin-bottom:5px; color:#1e3c72; }}
        .nickname {{ font-size:16px; text-align:center; color:#2575fc; margin-bottom:25px; font-weight:500; font-style:italic; }}
        .detail-item {{ display:flex; align-items:center; margin-bottom:18px; padding:12px; background:#f0f8ff; border-radius:10px; box-shadow:0 3px 8px rgba(0,0,0,0.05); }}
        .detail-icon {{ width:35px; text-align:center; color:#2575fc; margin-right:12px; font-size:18px; }}
        .detail-label {{ font-weight:600; color:#1e3c72; font-size:12px; text-transform:uppercase; letter-spacing:.5px; margin-bottom:3px; }}
        .detail-value {{ color:#2a5298; font-size:15px; word-break:break-all; }}
        .qr-section {{ text-align:center; padding:20px; background:#f0f8ff; margin-top:20px; border-radius:15px; border:2px dashed #2575fc; }}
        .qr-title {{ font-size:16px; font-weight:600; margin-bottom:15px; color:#1e3c72; }}
        #qrcode img {{ display:inline-block; padding:10px; background:#fff; border-radius:10px; box-shadow:0 3px 10px rgba(0,0,0,0.1); }}
        .footer {{ background:#1e3c72; padding:15px; text-align:center; font-size:12px; color:#fff; border-top:1px solid #2a5298; }}
    </style>
</head>
<body>
    <div class=""credential"">
        <div class=""header"">
            <div class=""keychain-icon"">🔑</div>
            <div class=""logo"">🔑</div>
            <h1>.</h1>
            <h1>.</h1>
            <h1>.</h1>
        </div>

        <div class=""photo-container"">
            {(string.IsNullOrWhiteSpace(photoDataUrl) ? "👤" : $"<img src=\"{photoDataUrl}\" alt=\"Foto\" />")}
        </div>

        <div class=""info"">
            <div class=""name"">{esc(fullName)}</div>
            <div class=""nickname"">@{esc(nickname)}</div>

            <div class=""detail-item"">
                <div class=""detail-icon"">✉️</div>
                <div class=""detail-content"">
                    <div class=""detail-label"">Correo Electrónico</div>
                    <div class=""detail-value"">{esc(email)}</div>
                </div>
            </div>
            <div class=""detail-item"">
                <div class=""detail-icon"">📞</div>
                <div class=""detail-content"">
                    <div class=""detail-label"">Número de Teléfono</div>
                    <div class=""detail-value"">{esc(phone)}</div>
                </div>
            </div>

            <div class=""qr-section"">
                <div class=""qr-title"">Código QR de Identificación</div>
                <div id=""qrcode"">
                    <img width=""120"" height=""120"" src=""data:image/png;base64,{qrBase64}"" alt=""QR"" />
                </div>
            </div>
        </div>

        <div class=""footer"">
            Presentar esta credencial para acceder a tu cuenta
        </div>
    </div>
</body>
</html>";
    }

    private static string BuildQrPngBase64(string text)
    {
        using var gen  = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.H);
        using var png  = new PngByteQRCode(data);
        var bytes = png.GetGraphic(6);
        return Convert.ToBase64String(bytes);
    }
}
