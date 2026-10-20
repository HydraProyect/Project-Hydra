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

    /// <summary>
    /// El perfil que la sesión traía antes de quedar limitada a <see cref="Roles.Consulta"/>.
    /// Nunca es rol efectivo: solo decide el alcance en Tenants beneficiarios delegados, para que
    /// la restricción no amplíe el de un Gestor CAE (acotado por cartera) al de Consulta.
    /// </summary>
    public const string TipoClaimPerfilAntesDeRestringir = "hydra:perfil_antes_de_restringir";

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

        // El perfil anterior a la restricción no autoriza nada: solo decide qué Tenants
        // beneficiarios delegados alcanza la sesión (decisión Chris 2026-10-20). Sin él, un
        // Gestor CAE entrado con contraseña pasaría a Consulta y, con ese perfil, alcanzaría sin
        // cartera todos los Tenants de la Asignación de Operación de su organización.
        if (!identidad.HasClaim(c => c.Type == TipoClaimPerfilAntesDeRestringir)
            // Con varios roles (estado que no debería existir) gana el más acotado.
            && identidad.FindAll(identidad.RoleClaimType).Select(c => c.Value)
                    .OrderBy(r => r == Roles.GestorCae ? 0 : 1).ThenBy(r => r, StringComparer.Ordinal).FirstOrDefault()
                is { } perfilAntes)
            identidad.AddClaim(new Claim(TipoClaimPerfilAntesDeRestringir, perfilAntes));

        foreach (var claimRol in identidad.FindAll(identidad.RoleClaimType).ToList())
            identidad.RemoveClaim(claimRol);

        identidad.AddClaim(new Claim(identidad.RoleClaimType, Roles.Consulta));

        return Task.FromResult(principal);
    }
}
