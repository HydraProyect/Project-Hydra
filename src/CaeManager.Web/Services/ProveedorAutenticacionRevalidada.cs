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
/// <b>Un error al validar no expulsa.</b> Si la base falla, se conserva el
/// estado y se reintenta en el siguiente ciclo: sin base tampoco se sirve
/// ningún dato, y expulsar a todos los circuitos en un parpadeo de la base
/// sería un incidente peor que el hueco de un ciclo. El intervalo es el de
/// <see cref="SecurityStampValidatorOptions.ValidationInterval"/>, el mismo
/// de la cookie, configurable con <c>Sesion:IntervaloRevalidacionSegundos</c>.
/// </para>
/// </summary>
public sealed class ProveedorAutenticacionRevalidada(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<SecurityStampValidatorOptions> opciones)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ProveedorAutenticacionRevalidada>();

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
            return await signInManager.ValidateSecurityStampAsync(authenticationState.User) is not null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo revalidar la sesión del circuito; se reintentará en el próximo ciclo.");
            return true;
        }
    }
}
