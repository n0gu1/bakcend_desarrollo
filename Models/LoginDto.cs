namespace BaseUsuarios.Api.Models
{
    public sealed class LoginDto
    {
        public string Credential { get; set; } = "";
        public string Password { get; set; } = "";
    }
}
