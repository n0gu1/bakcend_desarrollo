// BaseUsuarios.Api/Models/RegistrationReceiptDbPhotoDto.cs
using System.ComponentModel.DataAnnotations;

namespace BaseUsuarios.Api.Models;

public sealed class RegistrationReceiptDbPhotoDto
{
    // Identificador del destinatario del correo (obligatorio)
    [Required, EmailAddress] public string Email { get; set; } = string.Empty;

    // Para el contenido del PDF
    public string? FullName { get; set; }      // Si no viene, se usa Nickname
    public string? Nickname { get; set; }      // Para mostrar @nickname en el PDF
    public string? Phone { get; set; }

    // Para localizar al usuario en BD (usa el primero disponible)
    public long?   UserId { get; set; }
    public string? Credential { get; set; }    // puede ser Email / Teléfono / Nickname
}
