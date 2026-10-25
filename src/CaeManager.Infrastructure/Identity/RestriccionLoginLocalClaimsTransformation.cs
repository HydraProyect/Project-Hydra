using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Capa extra de control para roles editores: mientras el login por
/// Microsoft (Entra ID) esté configurado, una sesión de una cuenta cuyo Tenant
/// propietario es <see cref="AzureAdOptions.TenantHydraId"/> que no se haya
/// autenticado por ese camino queda sin escritura — un rol editor pasa a
/// <see cref="Roles.Consulta"/> (solo lectura) —, y nunca con más alcance que
/// su rol real: Cliente conserva su rol y una cuenta sin rol sigue sin él (ver
/// <c>RolLimitado</c>). El login local con contraseña sigue
/// funcionando (no se bloquea), pero deja de dar permisos de edición. Solo el
/// inicio con Microsoft añade <see cref="MetodoLoginSso"/> al iniciar sesión: el
/// callback (ver IdentityEndpointsExtensions) cuando Microsoft acredita la MFA, y
/// <see cref="SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft"/> en la
/// sesión que Identity emite tras el TOTP de TALVEG de ese mismo inicio (D-2, la
/// cookie de dos pasos que escribe el callback lleva el proveedor Microsoft).
/// <see cref="ConservarMetodoLoginAlRevalidar"/> la conserva al revalidar la cookie
/// y <see cref="SignInManagerCuentaDesactivada.RefreshSignInAsync"/> al reemitirla.
/// Inerte mientras <see cref="AzureAdOptions.EstaConfigurado"/> sea falso,
/// para no bloquear el login local antes de tener SSO activo (arranque en
/// frío del propio Administrador) — mismo principio "inerte por defecto"
/// que Sentry/Backups/Anthropic. **Excepción explícita**: Administrador
/// conserva su rol real incluso por login local — vía de escape deliberada
/// (pedida por el usuario) para nunca perder acceso de administración al
/// portal si algo falla del lado de Entra ID durante pruebas o en producción.
///
/// <para>
/// <b>Alcance (decisión D-1 de Chris, 2026-10-25).</b> El login con Microsoft
/// solo abre cuentas del Tenant propietario declarado en
/// <see cref="AzureAdOptions.TenantHydraId"/> (ver <c>InicioSesionMicrosoft</c>),
/// así que solo esas cuentas tienen la alternativa y solo a ellas se les quita la
/// escritura con contraseña. Una cuenta de otro Tenant propietario conserva su rol
/// íntegro: antes la restricción era global y quitaba la escritura a los Usuarios
/// de todos los Tenants. El Tenant propietario de la cuenta es el claim
/// <see cref="TenantClaimsPrincipalFactory.TipoClaimTenantId"/>, que sale de
/// <c>ApplicationUser.TenantId</c> y nunca es el Tenant beneficiario delegado que
/// la sesión esté operando: un Gestor CAE del Tenant declarado entrado con
/// contraseña queda en Consulta también en la Asignación de Operación.
/// </para>
///
/// <para>
/// <b>Fallo cerrado.</b> Se restringe salvo que conste, sin ambigüedad, que la
/// cuenta es de otro Tenant propietario: sin <see cref="AzureAdOptions.TenantHydraId"/>
/// declarado (nulo o <see cref="Guid.Empty"/>), la restricción sigue siendo
/// global, igual que el callback rechaza entonces a todo el mundo (lo que ya fijó
/// la PR #1650 al dejar ese valor fuera del gate); y sin claim de Tenant, con uno
/// ilegible, vacío o con varios, también se restringe. Restringir de más solo
/// quita escritura; no restringir abriría la escritura con contraseña a cuentas
/// que deben entrar por Microsoft.
/// </para>
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
    public const string TipoClaimPerfilAntesDeRestringir = "talveg:perfil_antes_de_restringir";

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
        if (EsDeOtroTenantPropietario(identidad, _azureAd.Value)) return Task.FromResult(principal);

        // El perfil anterior a la restricción no autoriza nada: solo decide qué Tenants
        // beneficiarios delegados alcanza la sesión (decisión Chris 2026-10-20). Sin él, un
        // Gestor CAE entrado con contraseña pasaría a Consulta y, con ese perfil, alcanzaría sin
        // cartera todos los Tenants de la Asignación de Operación de su organización. Se pone
        // SIEMPRE al restringir: sin rol previo, vacío, que se lee como «sin perfil» (fallo
        // cerrado). La cuenta sin rol ya no recibe Consulta (ver RolLimitado); el marcador vacío
        // queda como segunda barrera por si algún lector cayera al rol de sesión.
        var rolesReales = identidad.FindAll(identidad.RoleClaimType).Select(c => c.Value).ToList();

        if (!identidad.HasClaim(c => c.Type == TipoClaimPerfilAntesDeRestringir))
            identidad.AddClaim(new Claim(
                TipoClaimPerfilAntesDeRestringir,
                // Con varios roles (estado que no debería existir) gana el más acotado. Solo roles
                // conocidos: uno desconocido no es perfil y se lee como «sin perfil».
                rolesReales.Where(Roles.Todos.Contains)
                    .OrderBy(r => r switch { Roles.Cliente => 0, Roles.GestorCae => 1, _ => 2 })
                    .ThenBy(r => r, StringComparer.Ordinal).FirstOrDefault()
                ?? string.Empty));

        foreach (var claimRol in identidad.FindAll(identidad.RoleClaimType).ToList())
            identidad.RemoveClaim(claimRol);

        if (RolLimitado(rolesReales) is { } rolLimitado)
            identidad.AddClaim(new Claim(identidad.RoleClaimType, rolLimitado));

        return Task.FromResult(principal);
    }

    /// <summary>
    /// Engancha a <see cref="SecurityStampValidatorOptions.OnRefreshingPrincipal"/> la conservación de la
    /// marca <see cref="MetodoLoginSso"/>. El validador de la cookie rehace el principal con la fábrica de
    /// claims en cada intervalo de revalidación (60 s por defecto, <c>Sesion:IntervaloRevalidacionSegundos</c>)
    /// y descarta los claims que el callback de Microsoft añadió al iniciar sesión: sin esto, una sesión
    /// abierta por Microsoft perdía la marca en la primera revalidación y la restricción la dejaba en
    /// Consulta, también en el Tenant beneficiario delegado. Solo se copia la marca que ya traía la cookie
    /// validada, emitida por el servidor; nunca se crea donde no estaba. Respeta un gancho anterior.
    /// </summary>
    public static void ConservarMetodoLoginAlRevalidar(SecurityStampValidatorOptions opciones)
    {
        var anterior = opciones.OnRefreshingPrincipal;
        opciones.OnRefreshingPrincipal = async contexto =>
        {
            if (anterior is not null) await anterior(contexto);
            CopiarMetodoLoginSso(contexto);
        };
    }

    private static void CopiarMetodoLoginSso(SecurityStampRefreshingPrincipalContext contexto)
    {
        if (contexto.CurrentPrincipal?.HasClaim(TipoClaimMetodoLogin, MetodoLoginSso) == true
            && contexto.NewPrincipal?.Identity is ClaimsIdentity nueva
            && !nueva.HasClaim(TipoClaimMetodoLogin, MetodoLoginSso))
            nueva.AddClaim(new Claim(TipoClaimMetodoLogin, MetodoLoginSso));
    }

    /// <summary>
    /// Consta, sin ambigüedad, que el Tenant propietario de la cuenta no es el declarado en
    /// <see cref="AzureAdOptions.TenantHydraId"/>: hay Tenant declarado, la sesión trae un único claim
    /// de Tenant legible y no vacío, y es otro. En cualquier otro caso, falso (fallo cerrado: se
    /// restringe).
    /// </summary>
    private static bool EsDeOtroTenantPropietario(ClaimsIdentity identidad, AzureAdOptions azureAd)
    {
        if (!azureAd.TieneTenantDestino) return false;

        var claimsTenant = identidad.FindAll(TenantClaimsPrincipalFactory.TipoClaimTenantId).ToList();
        return claimsTenant.Count == 1
               && Guid.TryParse(claimsTenant[0].Value, out var tenantPropietario)
               && tenantPropietario != Guid.Empty
               && tenantPropietario != azureAd.TenantDestino;
    }

    /// <summary>
    /// El rol que deja la restricción: nunca uno con más alcance que el real, porque la restricción
    /// solo existe para quitar escritura (defecto corregido el 2026-10-22: un usuario de Cliente,
    /// acotado a su Cliente empresarial, pasaba a Consulta y veía el Tenant entero).
    /// <list type="bullet">
    /// <item>Cliente se conserva: ya es de solo lectura y su alcance, por
    /// <c>ApplicationUser.ClienteId</c>, solo existe con ese rol. Gana sobre cualquier otro rol que
    /// trajera la sesión (estado que no debería existir), por ser el más acotado.</item>
    /// <item>Los roles editores y Consulta quedan en Consulta (solo lectura).</item>
    /// <item>Sin rol, o solo con roles desconocidos, la sesión sigue sin rol (fallo cerrado): darle
    /// Consulta le abriría el Tenant entero.</item>
    /// </list>
    /// </summary>
    private static string? RolLimitado(IReadOnlyCollection<string> rolesReales) =>
        rolesReales.Contains(Roles.Cliente) ? Roles.Cliente
        : rolesReales.Any(r => r is Roles.DireccionCae or Roles.CoordinadorCae or Roles.GestorCae or Roles.Consulta) ? Roles.Consulta
        : null;
}
