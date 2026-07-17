using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Identity;

/// <summary>Usuario interno de CAE Manager. Extiende Identity solo con lo que ya se necesita en Fase 0.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string NombreCompleto { get; set; } = string.Empty;
    public TemaPreferido Tema { get; set; } = TemaPreferido.Sistema;
}
