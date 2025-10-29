namespace BaseUsuarios.Api.Models;

public sealed class RegisterDto
{
    public string   Email          { get; set; } = "";
    public string?  Phone          { get; set; }
    public DateTime? Birthdate     { get; set; }
    public string?  BirthdateText  { get; set; }
    public string   Nickname       { get; set; } = "";
    public string   Password       { get; set; } = "";
    public string?  PhotoBase64    { get; set; } // foto login (opcional)
    public string?  PhotoMime      { get; set; }
    public string?  Photo2Base64   { get; set; } // foto personalizada (opcional)
    public string?  Photo2Mime     { get; set; }
    public int      RoleId         { get; set; } = 2;
}