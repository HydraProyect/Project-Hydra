using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Services;

/// <summary>
/// Repite, mientras el circuito de Blazor está abierto, la comprobación que
/// la cookie hace en cada petición HTTP. Un circuito guarda el
/// <c>ClaimsPrincipal</c> del instante en que se conectó y, sin esto, lo
/// conservaba hasta cerrarse: una cuenta desactivada con el circuito abierto
/// siguió navegando, exportando y <b>creando datos nuevos</b> (auditoría
/// 2026-09-20). Cuando la cuenta ya no es válida (<see cref="SesionDeCuenta.SigueVigenteAsync"/>:
/// no existe, otro security stamp o bloqueada), el estado de autenticación del
/// circuito pasa a anónimo y las Commands fallan cerradas, que es el diseño del
/// propio <see cref="RevalidatingServerAuthenticationStateProvider"/>.
///
/// <para>
/// Se valida en un ámbito de DI <b>propio</b>, no en el del circuito: el
/// temporizador corre fuera del renderizado y el <c>DbContext</c> del circuito
/// no admite dos operaciones a la vez (ver <c>PuertaAccesoDatos</c>).
/// <c>AspNetUsers</c> no tiene RLS ni filtro global, así que ese ámbito no
/// necesita tenant para leer la cuenta. Un fallo transitorio al validar
/// <b>no</b> expulsa al usuario (el framework, si la excepción sale, lo trata
/// como sesión inválida): se registra y se reintenta en el siguiente ciclo, el
/// mismo criterio que <see cref="RevalidacionCircuitoActivoHandler"/>; el
/// validador de la cookie sigue siendo el respaldo en la siguiente petición HTTP.
/// </para>
/// </summary>
public sealed class RevalidadorDeAutenticacionDelCircuito(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IOptions<IdentityOptions> opcionesIdentity,
    IConfiguration configuracion)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<RevalidadorDeAutenticacionDelCircuito>();

    /// <summary>
    /// Mismo parámetro configurable que la revalidación de la delegación del
    /// Cliente activo (<see cref="RevalidacionCircuitoActivoHandler"/>): 60 s.
    /// </summary>
    protected override TimeSpan RevalidationInterval { get; } = TimeSpan.FromSeconds(
        Math.Max(1, configuracion.GetValue("Circuit:RevalidacionIntervaloSegundos", 60)));

    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true) return true;

        try
        {
            await using var ambito = scopeFactory.CreateAsyncScope();
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            return await SesionDeCuenta.SigueVigenteAsync(userManager, opcionesIdentity.Value, principal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo revalidar la sesión de la cuenta en el circuito; se reintentará en el próximo ciclo.");
            return true;
        }
    }
}
