namespace BaseUsuarios.Api.Models;

public sealed class UserCreateDto
{
    public string   Email        { get; set; } = "";
    public string?  Phone        { get; set; }
    public DateTime Birthdate    { get; set; }
    public string   Nickname     { get; set; } = "";
    public string   Password     { get; set; } = "";

    public string?  PhotoBase64  { get; set; }
    public string?  PhotoMime    { get; set; }

    public string?  Photo2Base64 { get; set; }
    public string?  Photo2Mime   { get; set; }

    public byte?    RoleId       { get; set; } // si no viene, usamos 2
}