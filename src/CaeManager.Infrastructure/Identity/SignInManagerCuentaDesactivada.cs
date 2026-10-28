using System.Globalization;
using System.Security.Claims;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Seguridad;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// <c>SignInManager</c> que hace que <b>desactivar una cuenta corte sus
/// sesiones</b>, no solo el inicio de sesión nuevo con contraseña.
///
/// <para>
/// Antes, «Desactivar» solo escribía <c>LockoutEnd = MaxValue</c>: impedía
/// entrar de nuevo, pero la cookie ya emitida (14 días deslizante) y el
/// circuito de Blazor ya conectado seguían leyendo, exportando y escribiendo,
/// porque el validador del security stamp compara únicamente el stamp
/// (auditoría de seguridad 2026-09-20). Este tipo reúne los dos puntos únicos
/// por los que pasa toda sesión de cookie:
/// </para>
///
/// <list type="bullet">
/// <item><b>Validación</b> — <see cref="ValidateSecurityStampAsync(ApplicationUser?, string?)"/>
/// la usan tanto el <c>SecurityStampValidator</c> de la cookie como el
/// proveedor de autenticación del circuito
/// (<c>ProveedorAutenticacionRevalidada</c>): una cuenta desactivada deja de
/// validar aunque su stamp coincida (cubre también cuentas desactivadas antes
/// de rotar el stamp al desactivar).</item>
/// <item><b>Emisión</b> — <see cref="SignInWithClaimsAsync(ApplicationUser, AuthenticationProperties?, IEnumerable{Claim})"/>
/// es a donde llegan <c>SignInAsync</c>, <c>RefreshSignInAsync</c> y el
/// callback de Microsoft (que no comprueba bloqueo). Sin este freno, una
/// cuenta desactivada con cookie aún sin revalidar cambiaba su contraseña,
/// <c>RefreshSignInAsync</c> le emitía una cookie nueva con el stamp nuevo, y
/// repetirlo cada menos de un intervalo la mantenía dentro para siempre.</item>
/// </list>
///
/// <para>
/// <b>Identificador de sesión (<see cref="ClaimSid"/>).</b> Cada emisión nueva de la cookie
/// lleva un identificador aleatorio de sesión, registrado en <c>SesionesUsuario</c>
/// (<see cref="IRegistroSesionesUsuario"/>); <c>RefreshSignInAsync</c> y la revalidación
/// periódica conservan el de la cookie. La validación exige que la cookie lo lleve y que su
/// sesión siga sin revocar: cerrar sesión revoca solo esa sesión, y una copia de la cookie deja
/// de valer en la siguiente revalidación. Una cookie emitida antes de este cambio no lo lleva y
/// se rechaza: al desplegarlo, todos vuelven a iniciar sesión una vez.
/// </para>
///
/// No toca autorización de datos, RLS ni roles: solo decide si hay o no un
/// principal autenticado. Sin principal, <c>TenantActual.TenantId</c> es nulo
/// y el filtro global de EF deniega, como con cualquier petición anónima.
/// </summary>
public class SignInManagerCuentaDesactivada(
    UserManager<ApplicationUser> userManager,
    IHttpContextAccessor contextAccessor,
    IUserClaimsPrincipalFactory<ApplicationUser> claimsFactory,
    IOptions<IdentityOptions> optionsAccessor,
    ILogger<SignInManager<ApplicationUser>> logger,
    IAuthenticationSchemeProvider schemes,
    IUserConfirmation<ApplicationUser> confirmation,
    IVigilanciaInicioSesion vigilancia,
    IRegistroSesionesUsuario sesiones)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>
    /// Tipo del claim con el identificador de sesión (Guid en formato <c>N</c>). Con prefijo propio y
    /// no <c>sid</c> a secas: ese nombre lo usan los tokens de Entra ID para su propia sesión. Solo lo
    /// escribe este tipo; cualquier otro que llegue se descarta.
    /// </summary>
    public const string ClaimSid = "talveg:sid";

    /// <summary>El identificador de sesión del principal, o nulo si no lleva uno legible.</summary>
    public static Guid? LeerSid(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ClaimSid)?.Value is { } valor
        && Guid.TryParseExact(valor, "N", out var sid)
        && sid != Guid.Empty
            ? sid
            : null;

    /// <summary>
    /// Punto de entrada de la validación periódica de la cookie y del circuito: busca
    /// la cuenta por el <c>Id</c> del principal. En el <c>SecurityStampValidator</c>
    /// de la cookie todavía no hay <c>HttpContext.User</c> y por tanto tampoco Tenant,
    /// y <c>AspNetUsers</c> tiene RLS (P1-M1): sin
    /// <see cref="AmbitoIdentificacionSinTenant"/> la cuenta no se encontraría y la
    /// sesión moriría en el primer refresco. Con Tenant en el contexto (el circuito
    /// ya autenticado) el ámbito no cambia nada: la cuenta se lee por la rama de la
    /// propia cuenta de la política.
    ///
    /// <para>
    /// Además del sello y de la desactivación, exige el identificador de sesión y que su sesión
    /// siga sin revocar (<c>app_sesion_vigente</c>, sin Tenant). Lo llaman los dos revalidadores,
    /// el de la cookie y el del circuito, con el mismo intervalo
    /// (<see cref="SecurityStampValidatorOptions.ValidationInterval"/>).
    /// </para>
    /// </summary>
    public override async Task<ApplicationUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        var usuario = await base.ValidateSecurityStampAsync(principal);
        if (usuario is null)
            return null;

        if (LeerSid(principal) is not { } sid)
        {
            Logger.LogWarning("Sesión rechazada: la cookie de la cuenta {UsuarioId} no lleva identificador de sesión.", usuario.Id);
            return null;
        }

        if (!await sesiones.EstaVigenteAsync(usuario.Id, sid))
        {
            Logger.LogWarning("Sesión rechazada: la sesión de la cuenta {UsuarioId} está revocada o no existe.", usuario.Id);
            return null;
        }

        return usuario;
    }

    public override async Task<bool> ValidateSecurityStampAsync(ApplicationUser? user, string? securityStamp)
    {
        if (user is not null && user.EstaDesactivada(DateTimeOffset.UtcNow))
        {
            Logger.LogWarning("Sesión rechazada: la cuenta {UsuarioId} está desactivada.", user.Id);
            return false;
        }

        return await base.ValidateSecurityStampAsync(user, securityStamp);
    }

    /// <summary>Lo pone <see cref="RefreshSignInAsync"/> mientras refresca una sesión que entró por Microsoft.</summary>
    private bool _conservarMarcaSso;

    /// <summary>
    /// Instante (segundos Unix, UTC) en que esta sesión superó el TOTP de TALVEG. Lo pone
    /// <see cref="TwoFactorAuthenticatorSignInAsync"/> al acertar el código y lo conserva
    /// <see cref="RefreshSignInAsync"/> con su valor original: refrescar la cookie no rejuvenece
    /// la verificación. Un segundo factor recordado en el navegador o un código de recuperación
    /// no la ponen, así que la apertura de una Sesión Privilegiada falla en cerrado.
    /// </summary>
    public const string ClaimTotpSesion = "talveg:totp_sesion";

    /// <summary>Antigüedad máxima del TOTP de la sesión para abrir una Sesión Privilegiada.</summary>
    public static readonly TimeSpan VentanaTotpReciente = TimeSpan.FromHours(12);

    /// <summary>Margen de reloj admitido para una marca con instante futuro.</summary>
    private static readonly TimeSpan MargenRelojTotp = TimeSpan.FromMinutes(5);

    private string? _marcaTotpConservada;
    private bool _verificandoTotp;

    /// <summary>
    /// Si <paramref name="principal"/> superó el TOTP hace como mucho <see cref="VentanaTotpReciente"/>.
    /// Falla en cerrado: sin marca, con un valor que no es un instante, o con un instante futuro
    /// más allá del margen de reloj.
    /// </summary>
    public static bool TotpEsReciente(ClaimsPrincipal? principal, DateTimeOffset ahora)
    {
        if (principal?.FindFirst(ClaimTotpSesion)?.Value is not { } valor
            || !long.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var segundos)
            || segundos > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
            return false;

        var verificado = DateTimeOffset.FromUnixTimeSeconds(segundos);
        return verificado <= ahora + MargenRelojTotp && ahora - verificado <= VentanaTotpReciente;
    }

    /// <summary>
    /// <c>RefreshSignInAsync</c> (cambiar la contraseña, activar el segundo factor) reemite la cookie
    /// conservando solo <c>amr</c> y el método de autenticación: perdía la marca
    /// <see cref="RestriccionLoginLocalClaimsTransformation.MetodoLoginSso"/> y un Gestor CAE del Tenant
    /// declarado que había entrado por Microsoft quedaba en Consulta. Se conserva solo si la cookie
    /// autenticada de esta petición ya la traía y es de la misma cuenta; nunca se crea donde no estaba.
    /// </summary>
    public override async Task RefreshSignInAsync(ApplicationUser user)
    {
        var autenticada = await Context.AuthenticateAsync(AuthenticationScheme);
        _conservarMarcaSso = autenticada.Principal is { } actual
            && actual.HasClaim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin,
                RestriccionLoginLocalClaimsTransformation.MetodoLoginSso)
            && UserManager.GetUserId(actual) == user.Id.ToString();
        var mismaCuenta = autenticada.Principal is { } principal
            && UserManager.GetUserId(principal) == user.Id.ToString();
        _marcaTotpConservada = mismaCuenta ? autenticada.Principal!.FindFirst(ClaimTotpSesion)?.Value : null;
        // Refrescar la cookie no abre una sesión nueva: conserva su identificador (y con él su
        // revocación). Sin cookie de la misma cuenta, la emisión registra una sesión nueva.
        _sidConservado = mismaCuenta ? LeerSid(autenticada.Principal) : null;
        try
        {
            await base.RefreshSignInAsync(user);
        }
        finally
        {
            _conservarMarcaSso = false;
            _marcaTotpConservada = null;
            _sidConservado = null;
        }
    }

    private Guid? _sidConservado;

    /// <summary>
    /// Engancha a <see cref="SecurityStampValidatorOptions.OnRefreshingPrincipal"/> la conservación de la
    /// marca de TOTP, igual que <see cref="RestriccionLoginLocalClaimsTransformation.ConservarMetodoLoginAlRevalidar"/>
    /// con la de Microsoft: el validador de la cookie rehace el principal cada intervalo de revalidación
    /// (60 s por defecto) y sin esto la marca moría en el primer minuto. Copia el valor original de la
    /// cookie validada, de la misma cuenta, sin rejuvenecerlo; nunca la crea donde no estaba.
    /// Respeta un gancho anterior.
    /// </summary>
    public static void ConservarMarcaTotpAlRevalidar(SecurityStampValidatorOptions opciones)
    {
        var anterior = opciones.OnRefreshingPrincipal;
        opciones.OnRefreshingPrincipal = async contexto =>
        {
            if (anterior is not null) await anterior(contexto);
            CopiarMarcaTotp(contexto);
        };
    }

    private static void CopiarMarcaTotp(SecurityStampRefreshingPrincipalContext contexto)
    {
        if (contexto.NewPrincipal?.Identity is not ClaimsIdentity nueva) return;
        foreach (var ajena in nueva.FindAll(ClaimTotpSesion).ToList())
            nueva.RemoveClaim(ajena);

        var actual = contexto.CurrentPrincipal;
        if (actual?.FindFirst(ClaimTotpSesion)?.Value is { } marca
            && actual.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } cuentaActual
            && cuentaActual == contexto.NewPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            nueva.AddClaim(new Claim(ClaimTotpSesion, marca));
    }

    /// <summary>
    /// Engancha a <see cref="SecurityStampValidatorOptions.OnRefreshingPrincipal"/> la conservación del
    /// identificador de sesión (<see cref="ClaimSid"/>), con el mismo criterio que
    /// <see cref="ConservarMarcaTotpAlRevalidar"/>: el validador de la cookie rehace el principal cada
    /// intervalo de revalidación, y sin esto la cookie renovada saldría sin identificador y la sesión
    /// moriría en el minuto siguiente. Copia el de la cookie validada, de la misma cuenta; descarta
    /// cualquiera que traiga el principal rehecho y nunca lo crea donde no estaba. Respeta un gancho anterior.
    /// </summary>
    public static void ConservarSidAlRevalidar(SecurityStampValidatorOptions opciones)
    {
        var anterior = opciones.OnRefreshingPrincipal;
        opciones.OnRefreshingPrincipal = async contexto =>
        {
            if (anterior is not null) await anterior(contexto);
            CopiarSid(contexto);
        };
    }

    private static void CopiarSid(SecurityStampRefreshingPrincipalContext contexto)
    {
        if (contexto.NewPrincipal?.Identity is not ClaimsIdentity nueva) return;
        foreach (var ajeno in nueva.FindAll(ClaimSid).ToList())
            nueva.RemoveClaim(ajeno);

        var actual = contexto.CurrentPrincipal;
        if (LeerSid(actual) is { } sid
            && actual!.FindFirst(ClaimTypes.NameIdentifier)?.Value is { } cuentaActual
            && cuentaActual == contexto.NewPrincipal.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            nueva.AddClaim(new Claim(ClaimSid, sid.ToString("N")));
    }

    /// <summary>
    /// Una sesión nueva emitida sobre una cookie de aplicación que todavía vale (iniciar sesión otra vez,
    /// o con otra cuenta, sin cerrar antes) sustituye a la de esa cookie en este navegador: se revoca su
    /// identificador, para que una copia de la cookie anterior no siga valiendo hasta su caducidad. La
    /// revoca la cuenta de la cookie anterior, que es quien firma el contexto de esta petición
    /// (<c>solo_la_propia_cuenta_revoca</c>). Sin cookie anterior, o sin identificador en ella, no hace
    /// nada. Un fallo se registra y no impide el inicio de sesión: la cookie anterior se sobrescribe igual.
    /// </summary>
    private async Task RevocarSesionQueSustituyeAsync()
    {
        try
        {
            var anterior = await Context.AuthenticateAsync(AuthenticationScheme);
            if (anterior.Principal is not { } principal
                || LeerSid(principal) is not { } sidAnterior
                || !Guid.TryParse(UserManager.GetUserId(principal), out var cuentaAnterior))
                return;
            await sesiones.RevocarAsync(cuentaAnterior, sidAnterior);
        }
        catch (Exception ex)
        {
            Logger.LogWarning("No se pudo revocar la sesión que sustituye el nuevo inicio de sesión ({TipoError}).",
                ex.GetType().Name);
        }
    }

    public override async Task SignInWithClaimsAsync(
        ApplicationUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        // La marca y el identificador de sesión solo los escribe este tipo: se descarta cualquier otro que llegue.
        additionalClaims = additionalClaims.Where(c => c.Type != ClaimTotpSesion && c.Type != ClaimSid);
        var marcaTotp = _verificandoTotp
            ? DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            : _marcaTotpConservada;
        if (marcaTotp is not null)
            additionalClaims = additionalClaims.Append(new Claim(ClaimTotpSesion, marcaTotp));
        if (_conservarMarcaSso && !additionalClaims.Any(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin))
            additionalClaims = additionalClaims.Append(new Claim(
                RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso));

        if (user.EstaDesactivada(DateTimeOffset.UtcNow))
        {
            // No se lanza: quien llega aquí desde CambiarContrasena o desde el
            // 2FA solo quería refrescar una cookie, y la cuenta desactivada ya
            // no debe tenerla; la cookie vieja muere en la siguiente validación.
            Logger.LogWarning("No se emite sesión: la cuenta {UsuarioId} está desactivada.", user.Id);
            return;
        }

        // Una sesión nueva (contraseña, segundo factor, callback de Microsoft) se registra antes de
        // emitir la cookie; si el registro falla, no se emite. Un refresco conserva la suya.
        var sid = _sidConservado;
        if (sid is { } conservado && !await sesiones.EstaVigenteAsync(user.Id, conservado))
        {
            // Un refresco (cambiar la contraseña, activar el segundo factor) no reemite la cookie de una
            // sesión revocada: con un IssuedUtc nuevo aplazaría su rechazo otro intervalo de
            // revalidación, y repetirlo la mantendría viva. Mismo criterio que la cuenta desactivada:
            // no se lanza, y la cookie vieja muere en la siguiente validación.
            Logger.LogWarning("No se refresca la sesión: la sesión de la cuenta {UsuarioId} está revocada.", user.Id);
            return;
        }
        if (sid is null)
        {
            sid = Guid.NewGuid();
            await sesiones.RegistrarAsync(user.Id, user.TenantId, sid.Value);
            await RevocarSesionQueSustituyeAsync();
        }
        additionalClaims = additionalClaims.Append(new Claim(ClaimSid, sid.Value.ToString("N")));

        await base.SignInWithClaimsAsync(user, authenticationProperties, ConMarcaDeInicioConMicrosoft(additionalClaims));
    }

    /// <summary>
    /// Una sesión que Identity emite con el proveedor externo de Microsoft (tras el TOTP
    /// de TALVEG del inicio con Microsoft, D-2, o al refrescarla) lleva la marca de
    /// inicio con SSO, como la que emite directamente el callback. Sin ella,
    /// <see cref="RestriccionLoginLocalClaimsTransformation"/> la trataría como un
    /// inicio con contraseña. El proveedor solo lo escribe la cookie de dos pasos que
    /// firma el callback, o la propia sesión al refrescarse.
    /// </summary>
    public static IEnumerable<Claim> ConMarcaDeInicioConMicrosoft(IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var lista = claims.ToList();
        var porMicrosoft = lista.Any(c => c.Type == ClaimTypes.AuthenticationMethod && c.Value == AzureAdOptions.Esquema);
        var yaMarcada = lista.Any(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin);
        if (porMicrosoft && !yaMarcada)
            lista.Add(new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso));
        return lista;
    }

    // ── Registro de inicios de sesión y bloqueo por país ─────────────────────
    //
    // Los pasos con credencial de Identity (contraseña y segundo factor) pasan
    // por aquí, aciertan o fallen: registrarlos en este tipo, y no en cada
    // página, hace que una página de login nueva no se quede fuera por olvido.
    // El callback de Microsoft no usa estos métodos y registra por su cuenta
    // (IdentityEndpointsExtensions). Bloqueo por país: CanSignInAsync, que
    // Identity consulta antes de comprobar la contraseña y otra vez en el
    // segundo factor; un rechazo sale como IsNotAllowed, con el mismo mensaje
    // genérico que cualquier otro fallo.

    private bool _cuentaEncontrada;
    private bool _paisNoPermitido;

    public override async Task<bool> CanSignInAsync(ApplicationUser user)
    {
        if (!await base.CanSignInAsync(user))
            return false;
        if (await vigilancia.PermiteOrigenAsync(user.Id, user.TenantId))
            return true;
        _paisNoPermitido = true;
        return false;
    }

    public override async Task<SignInResult> PasswordSignInAsync(string userName, string password, bool isPersistent, bool lockoutOnFailure)
    {
        _cuentaEncontrada = false;
        var resultado = await base.PasswordSignInAsync(userName, password, isPersistent, lockoutOnFailure);
        // Cuenta inexistente: la base devolvió Failed sin llegar a la
        // sobrecarga con usuario. Solo cuenta para la ráfaga por IP.
        if (!_cuentaEncontrada)
            await vigilancia.RegistrarAsync(null, null, MetodoInicioSesion.Contrasena, ResultadoInicioSesion.Fallido);
        return resultado;
    }

    public override async Task<SignInResult> PasswordSignInAsync(ApplicationUser user, string password, bool isPersistent, bool lockoutOnFailure)
    {
        _cuentaEncontrada = true;
        _paisNoPermitido = false;
        var resultado = await base.PasswordSignInAsync(user, password, isPersistent, lockoutOnFailure);
        await RegistrarAsync(user, MetodoInicioSesion.Contrasena, resultado);
        return resultado;
    }

    public override async Task<SignInResult> TwoFactorAuthenticatorSignInAsync(string code, bool isPersistent, bool rememberClient)
    {
        var usuario = await GetTwoFactorAuthenticationUserAsync();
        _paisNoPermitido = false;
        SignInResult resultado;
        _verificandoTotp = true;
        try
        {
            resultado = await base.TwoFactorAuthenticatorSignInAsync(code, isPersistent, rememberClient);
        }
        finally
        {
            _verificandoTotp = false;
        }
        if (usuario is not null)
            await RegistrarAsync(usuario, MetodoInicioSesion.SegundoFactor, resultado);
        return resultado;
    }

    public override async Task<SignInResult> TwoFactorRecoveryCodeSignInAsync(string recoveryCode)
    {
        var usuario = await GetTwoFactorAuthenticationUserAsync();
        _paisNoPermitido = false;
        var resultado = await base.TwoFactorRecoveryCodeSignInAsync(recoveryCode);
        if (usuario is not null)
            await RegistrarAsync(usuario, MetodoInicioSesion.CodigoRecuperacion, resultado);
        return resultado;
    }

    private Task RegistrarAsync(ApplicationUser user, MetodoInicioSesion metodo, SignInResult resultado)
    {
        var registrado =
            resultado.Succeeded ? ResultadoInicioSesion.Correcto
            : resultado.RequiresTwoFactor ? ResultadoInicioSesion.SegundoFactorRequerido
            : resultado.IsLockedOut ? ResultadoInicioSesion.Bloqueado
            : resultado.IsNotAllowed ? ResultadoInicioSesion.Rechazado
            : ResultadoInicioSesion.Fallido;
        var senales = _paisNoPermitido && resultado.IsNotAllowed ? SenalesInicioSesion.PaisNoPermitido : SenalesInicioSesion.Ninguna;
        return vigilancia.RegistrarAsync(user.Id, user.TenantId, metodo, registrado, senales);
    }
}
