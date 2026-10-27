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
    IVigilanciaInicioSesion vigilancia)
    : SignInManager<ApplicationUser>(userManager, contextAccessor, claimsFactory, optionsAccessor, logger, schemes, confirmation)
{
    /// <summary>
    /// Punto de entrada de la validación periódica de la cookie y del circuito: busca
    /// la cuenta por el <c>Id</c> del principal. En el <c>SecurityStampValidator</c>
    /// de la cookie todavía no hay <c>HttpContext.User</c> y por tanto tampoco Tenant,
    /// y <c>AspNetUsers</c> tiene RLS (P1-M1): sin
    /// <see cref="AmbitoIdentificacionSinTenant"/> la cuenta no se encontraría y la
    /// sesión moriría en el primer refresco. Con Tenant en el contexto (el circuito
    /// ya autenticado) el ámbito no cambia nada: la cuenta se lee por la rama de la
    /// propia cuenta de la política.
    /// </summary>
    public override async Task<ApplicationUser?> ValidateSecurityStampAsync(ClaimsPrincipal? principal)
    {
        using var identificacion = AmbitoIdentificacionSinTenant.Abrir();
        return await base.ValidateSecurityStampAsync(principal);
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
        _marcaTotpConservada = autenticada.Principal is { } principal
            && UserManager.GetUserId(principal) == user.Id.ToString()
                ? principal.FindFirst(ClaimTotpSesion)?.Value
                : null;
        try
        {
            await base.RefreshSignInAsync(user);
        }
        finally
        {
            _conservarMarcaSso = false;
            _marcaTotpConservada = null;
        }
    }

    public override Task SignInWithClaimsAsync(
        ApplicationUser user, AuthenticationProperties? authenticationProperties, IEnumerable<Claim> additionalClaims)
    {
        // La marca solo la escribe este tipo: se descarta cualquier otra que llegue.
        additionalClaims = additionalClaims.Where(c => c.Type != ClaimTotpSesion);
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
            return Task.CompletedTask;
        }

        return base.SignInWithClaimsAsync(user, authenticationProperties, ConMarcaDeInicioConMicrosoft(additionalClaims));
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
