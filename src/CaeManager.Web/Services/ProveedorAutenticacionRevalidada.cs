using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Services;

/// <summary>
/// Repite, desde dentro del circuito de Blazor, la validación de sesión que
/// la cookie solo hace en peticiones HTTP. Un circuito conserva el
/// <c>ClaimsPrincipal</c> del instante en que se conectó hasta que se cierra
/// (sin tope de duración mientras siga conectado), y todo lo que va por
/// SignalR no genera ninguna petición: sin esto, una cuenta desactivada o con
/// el stamp rotado seguía leyendo y escribiendo en su circuito ya abierto
/// (auditoría de seguridad 2026-09-20).
///
/// <para>
/// Cada <see cref="RevalidationInterval"/> abre un ámbito propio y pregunta a
/// <see cref="SignInManagerCuentaDesactivada"/> —la misma pregunta que hace la cookie— si
/// el principal sigue valiendo. Si no, el estado pasa a anónimo: el
/// <c>TenantActual</c> queda sin tenant, el filtro global de EF deniega
/// y <c>CurrentUserService</c> no devuelve usuario, así que las Commands
/// fallan cerradas — el mismo camino que ya sigue cualquier petición sin
/// sesión.
/// </para>
///
/// <para>
/// <b>Un error al validar falla cerrado.</b> Si la base falla (también con un
/// tiempo de espera del proveedor que llegue como cancelación con un token que no
/// es el del ciclo), no se puede afirmar que la sesión siga valiendo: se marca
/// invalidada y el circuito pasa a anónimo, con un aviso en el log que solo lleva
/// el tipo de la excepción (su mensaje puede arrastrar datos de la consulta). La
/// cookie no se toca: al recargar, sigue su propia validación por petición. La
/// cancelación del propio ciclo (circuito que se cierra o se desconecta, ciclo
/// nuevo) no invalida nada. El intervalo es el de
/// <see cref="SecurityStampValidatorOptions.ValidationInterval"/>, el mismo de la
/// cookie, configurable con <c>Sesion:IntervaloRevalidacionSegundos</c>.
/// </para>
/// </summary>
public sealed class ProveedorAutenticacionRevalidada(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityStampValidatorOptions> opciones)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory), ISesionDeCircuitoInvalidable
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProveedorAutenticacionRevalidada>();
    private volatile bool _sesionInvalidada;

    public bool SesionInvalidada => _sesionInvalidada;

    protected override TimeSpan RevalidationInterval => opciones.Value.ValidationInterval;

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        // Un circuito anónimo (pantalla de acceso) no tiene nada que validar;
        // sin esto, cada ciclo lo «invalidaría» y provocaría un render inútil.
        if (authenticationState.User.Identity?.IsAuthenticated != true)
            return true;

        try
        {
            await using var ambito = scopeFactory.CreateAsyncScope();
            var signInManager = ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            var vigente = await signInManager.ValidateSecurityStampAsync(authenticationState.User) is not null;
            if (!vigente) _sesionInvalidada = true;
            return vigente;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelación pedida por el propio bucle base (circuito que se cierra o ciclo nuevo).
            // El bucle base (RevalidationLoop) solo toma por cierre normal una
            // TaskCanceledException con SU token; cualquier otra excepción —incluida una
            // OperationCanceledException con el token correcto— cae en ForceSignOut() y deja
            // el estado anónimo aunque acabe de instalarse una autenticación nueva. Por eso
            // no se relanza la excepción original (que puede venir del proveedor de base de
            // datos con otro tipo u otro token) sino una equivalente que el bucle reconoce.
            throw new TaskCanceledException(null, null, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("No se pudo revalidar la sesión del circuito ({TipoError}); se invalida la sesión del circuito.",
                ex.GetType().Name);
            _sesionInvalidada = true;
            return false;
        }
    }
}

/// <summary>
/// Un proveedor de autenticación de circuito que sabe si su sesión dejó de
/// valer. Lo consultan <c>TenantActual</c> y <c>CurrentUserService</c> ANTES
/// de caer a <c>IHttpContextAccessor</c>: dentro de un circuito el
/// <c>HttpContext</c> es el de la petición que lo abrió (medido: sigue
/// presente y autenticado, con el usuario de entonces), así que sin esto un
/// circuito ya invalidado recuperaba su identidad por el fallback pensado para
/// los endpoints sin circuito, y seguía sirviendo datos. Ver
/// <see cref="ProveedorAutenticacionRevalidada"/>.
/// </summary>
public interface ISesionDeCircuitoInvalidable
{
    bool SesionInvalidada { get; }
}
