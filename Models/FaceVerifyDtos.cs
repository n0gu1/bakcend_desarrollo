// BaseUsuarios.Api/Models/FaceVerifyDtos.cs
namespace BaseUsuarios.Api.Models
{
    public sealed class FaceVerifyRequest
    {
        public string RostroA { get; set; } = "";
        public string RostroB { get; set; } = "";
    }

    public sealed class FaceVerifyResponse
    {
        public bool resultado { get; set; }
        public bool coincide  { get; set; }
        public string score   { get; set; } = "0";
        public string status  { get; set; } = "";
        public string error   { get; set; } = "";
    }
}
