namespace BaseUsuarios.Api.Models;

public sealed class UserPhotoDto
{
    public long    UserId      { get; set; }
    public string? Mime        { get; set; }
    public string? PhotoBase64 { get; set; }
}
