// BaseUsuarios.Api/Models/LoginCameraDto.cs
namespace BaseUsuarios.Api.Models
{
    public sealed class LoginCameraDto
    {
        public string Credential { get; set; } = "";
        public string Password   { get; set; } = "";
        public string? PhotoBase64 { get; set; }
        public string? PhotoMime   { get; set; }
    }
}
