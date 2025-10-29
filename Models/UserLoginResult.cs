namespace BaseUsuarios.Api.Models;

public sealed class UserLoginResult
{
    public bool   Success { get; set; }
    public long   UserId  { get; set; }
    public string Message { get; set; } = "";
}
