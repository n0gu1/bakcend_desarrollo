namespace BaseUsuarios.Api.Models;

public sealed class UtilSegmentRequest
{
    public string? PhotoBase64 { get; set; } // dataURL o base64
}

public sealed class FaceSegRequest
{
    public string RostroA { get; set; } = ""; // base64 sin prefijo data:
}

public sealed class FaceSegResponse
{
    public bool   resultado  { get; set; }
    public bool   segmentado { get; set; }
    public string? rostro    { get; set; }   // base64 del rostro
}

public sealed class FaceSegConfig
{
    public bool   Enabled        { get; set; } = true;
    public string Endpoint       { get; set; } = "";
    public int    TimeoutSeconds { get; set; } = 8;
}
