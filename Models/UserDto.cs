namespace BaseUsuarios.Api.Models;

public sealed class UserDto
{
    public long      Id         { get; set; }
    public string    Email      { get; set; } = "";
    public string?   Phone      { get; set; }
    public DateTime? Birthdate  { get; set; }
    public string    Nickname   { get; set; } = "";
    public bool      Activo     { get; set; }
}
