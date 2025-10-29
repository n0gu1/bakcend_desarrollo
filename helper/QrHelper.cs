// Helpers/QrHelper.cs
using QRCoder;

public static class QrHelper
{
    public static byte[] CreatePng(string text, int pixelsPerModule = 8)
    {
        var gen = new QRCodeGenerator();
        var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png  = new PngByteQRCode(data);
        return png.GetGraphic(pixelsPerModule);
    }
}
