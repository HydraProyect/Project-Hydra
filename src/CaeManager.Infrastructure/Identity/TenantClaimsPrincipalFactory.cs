using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Añade el claim <c>tenant_id</c> al construir el <see cref="ClaimsPrincipal"/>
/// de la sesión (ver docs/MULTITENANCY.md § 8, Tenant Resolution Strategy) —
/// se ejecuta en <c>SignInManager.SignInAsync</c> y en cada refresco
/// periódico del ticket de autenticación (mismo ciclo que ya refresca los
/// claims de rol). El valor sale directamente de <c>user.TenantId</c>, ya
/// cargado en memoria — no hace falta una consulta adicional a base de
/// datos, y no hay ningún riesgo de recursión con el filtro global de
/// <c>CaeManagerDbContext</c> (que no se aplica a <c>AspNetUsers</c>,
/// precisamente para que el login pueda resolver el tenant antes de
/// conocerlo).
/// </summary>
public class TenantClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(userManager, roleManager, optionsAccessor)
{
    public const string TipoClaimTenantId = "tenant_id";

    /// <summary>
    /// La cuenta tiene una obligación pendiente que la deja a medio activar:
    /// una contraseña temporal sin cambiar, o el rol Administrador sin la
    /// autenticación en dos pasos que ese rol exige (P1-13).
    ///
    /// <para>
    /// <b>Por qué viaja en el ticket y no se consulta.</b> Lo comprueba un
    /// middleware en cada petición autenticada, y consultar la base ahí pondría
    /// I/O en el camino caliente de todas ellas — lo mismo que
    /// <c>ITenantActual</c> evita a propósito. El valor se recalcula en cada
    /// <c>SignInAsync</c> y en el refresco periódico del ticket que instala
    /// <c>AddIdentityCookies</c>, así que se auto-sana; y los dos flujos que lo
    /// resuelven —cambiar la contraseña y activar 2FA— llaman a
    /// <c>RefreshSignInAsync</c>, de modo que el desbloqueo es inmediato y no
    /// espera a ese refresco.
    /// </para>
    ///
    /// <para>
    /// Si se quedara rancio, se equivoca hacia <b>seguir exigiendo</b> algo ya
    /// cumplido: un incordio, nunca un acceso de más.
    /// </para>
    /// </summary>
    public const string TipoClaimRequiereActivacion = "requiere_activacion";

    /// <summary>
    /// DEC-36 (REC-099): «permiso específico», no el rol Administrador a
    /// secas — ver <see cref="Policies.ConsultarAccesoDocumentosSensibles"/>.
    /// Solo se añade cuando está concedido (mismo motivo que
    /// <see cref="TipoClaimRequiereActivacion"/>: la política comprueba
    /// presencia del claim, no su valor, así que un claim "false" añadido
    /// siempre sería un permiso de más si alguien lo comprobara mal).
    /// </summary>
    public const string TipoClaimPermisoConsultarAccesoDocumentosSensibles = "permiso_consultar_acceso_documentos_sensibles";

    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);

        if (principal.Identity is not ClaimsIdentity identidad)
            return principal;

        identidad.AddClaim(new Claim(TipoClaimTenantId, user.TenantId.ToString()));

        if (user.PermisoConsultarAccesoDocumentosSensibles)
            identidad.AddClaim(new Claim(TipoClaimPermisoConsultarAccesoDocumentosSensibles, "true"));

        // El rol se lee del principal que acaba de construir la clase base: es
        // el rol ALMACENADO de la cuenta, no el efectivo de un workspace
        // delegado. Es el correcto para esto — la 2FA es una propiedad de la
        // cuenta, y quien es Administrador en su tenant debe tenerla aunque
        // ahora mismo esté operando otro como Consulta.
        var esAdministrador = principal.IsInRole(Roles.Administrador);

        if (user.DebeCambiarContrasena || (esAdministrador && !user.TwoFactorEnabled))
            identidad.AddClaim(new Claim(TipoClaimRequiereActivacion, "true"));

        return principal;
    }
}
