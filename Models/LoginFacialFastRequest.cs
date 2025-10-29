// BaseUsuarios.Api/Models/LoginFacialFastRequest.cs
namespace BaseUsuarios.Api.Models
{
    /// <summary>
    /// Modo rápido: compara la selfie (RostroA) contra un solo candidato en BD (RostroB).
    /// </summary>
    public sealed class LoginFacialFastRequest
    {
        /// <summary>Selfie en dataURL ("data:image/...;base64,AAA...") o base64 puro.</summary>
        public string PhotoBase64 { get; set; } = "";

        /// <summary>(Opcional) Forzar el candidato por UsuarioId.</summary>
        public ulong? UserId { get; set; }

        /// <summary>(Opcional) Forzar el candidato por credencial (email / nickname / teléfono).</summary>
        public string? Credential { get; set; }

        /// <summary>Porcentaje mínimo para aceptar (score/2 con tope 100). Default 60.</summary>
        public double MinPercent { get; set; } = 60;
    }
}
