using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Capa extra de control para roles editores: mientras el login por
/// Microsoft (Entra ID) esté configurado, cualquier sesión que no se haya
/// autenticado por ese camino queda con el rol efectivo limitado a
/// <see cref="Roles.Consulta"/> (solo lectura), sin importar el rol real
/// guardado en base de datos — el login local con contraseña sigue
/// funcionando (no se bloquea), pero deja de dar permisos de edición. El
/// callback de Microsoft (ver IdentityEndpointsExtensions) es el único
/// lugar que añade <see cref="ClaimMetodoLoginSso"/> al iniciar sesión.
/// Inerte mientras <see cref="AzureAdOptions.EstaConfigurado"/> sea falso,
/// para no bloquear el login local antes de tener SSO activo (arranque en
/// frío del propio Administrador) — mismo principio "inerte por defecto"
/// que Sentry/Backups/Anthropic. **Excepción explícita**: Administrador
/// conserva su rol real incluso por login local — vía de escape deliberada
/// (pedida por el usuario) para nunca perder acceso de administración al
/// portal si algo falla del lado de Entra ID durante pruebas o en producción.
/// </summary>
public class RestriccionLoginLocalClaimsTransformation : IClaimsTransformation
{
    public const string TipoClaimMetodoLogin = "metodo_login";
    public const string MetodoLoginSso = "sso";

    private readonly IOptions<AzureAdOptions> _azureAd;

    public RestriccionLoginLocalClaimsTransformation(IOptions<AzureAdOptions> azureAd)
    {
        _azureAd = azureAd;
    }

    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (!_azureAd.Value.EstaConfigurado) return Task.FromResult(principal);
        if (principal.Identity is not ClaimsIdentity identidad || !identidad.IsAuthenticated) return Task.FromResult(principal);
        if (identidad.HasClaim(TipoClaimMetodoLogin, MetodoLoginSso)) return Task.FromResult(principal);
        if (identidad.HasClaim(identidad.RoleClaimType, Roles.Administrador)) return Task.FromResult(principal);

        foreach (var claimRol in identidad.FindAll(identidad.RoleClaimType).ToList())
            identidad.RemoveClaim(claimRol);

        identidad.AddClaim(new Claim(identidad.RoleClaimType, Roles.Consulta));

        return Task.FromResult(principal);
    }
}
