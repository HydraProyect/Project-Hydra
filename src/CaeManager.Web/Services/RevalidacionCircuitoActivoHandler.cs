using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CaeManager.Web.Services;

/// <summary>
/// Repite periódicamente, desde dentro del propio circuito de Blazor Server,
/// la comprobación que <see cref="RevalidacionClienteActivoMiddleware"/> solo
/// podía hacer en peticiones HTTP — un circuito ya abierto que interactúa
/// puramente por SignalR no genera ninguna, así que una delegación revocada
/// mientras el usuario seguía con el workspace abierto no se notaba hasta la
/// siguiente navegación (hallazgo del Módulo 9, auditoría 2026-08-30). La
/// escritura ya estaba cerrada al instante por
/// <c>AutorizacionEscrituraBehavior</c>; lo que quedaba abierto era la
/// lectura dentro de ese circuito ya vivo.
///
/// Registrado como <b>Scoped</b>, no Singleton (a diferencia de
/// <see cref="MetricasCircuitHandler"/>): en Blazor Server el scope de DI
/// scoped es el del circuito, así que esto crea una instancia por circuito
/// con acceso directo a sus servicios scoped — exactamente el mismo patrón
/// que <see cref="CurrentUserService.ObtenerUsuarioAsync"/> ya documenta
/// para <c>AuthenticationStateProvider</c>: dentro de un circuito funciona
/// sin necesitar un <c>HttpContext</c> activo, que es justo lo que no existe
/// mientras el temporizador de fondo corre entre peticiones.
///
/// Solo muta estado en memoria (<see cref="ClienteActivoSeleccionado.Invalidar"/>)
/// — nunca navega ni cierra el circuito desde aquí: <c>NavigationManager</c>
/// y cualquier operación que dispare un render tienen afinidad con el
/// renderer del circuito, y llamarlas desde un temporizador en segundo plano
/// arriesgaría una excepción de hilo peor que el hueco que se cierra. La
/// próxima interacción del usuario (click, navegación, envío de formulario)
/// ya despacha su Query/Command con la selección invalidada, y el filtro
/// global de EF Core deniega por tenant nulo — fallo cerrado, igual que el
/// resto de <see cref="ClienteActivoSeleccionado"/>.
/// </summary>
public class RevalidacionCircuitoActivoHandler(
    IClienteActivoSeleccionado clienteActivoSeleccionado,
    ICurrentUserService currentUserService,
    ITenantsQueryContext dbContext,
    IOperacionesQueryContext operacionesContext,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    IConfiguration configuracion,
    ILogger<RevalidacionCircuitoActivoHandler> logger) : CircuitHandler, IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _bucle;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Mismo patrón configurable que Circuit:* en Program.cs: ajustable en
        // producción sin recompilar. 60 s por defecto — frecuente sin llegar
        // a competir de forma apreciable con las consultas propias del
        // usuario por PuertaAccesoDatos.
        var intervalo = TimeSpan.FromSeconds(
            configuracion.GetValue("Circuit:RevalidacionIntervaloSegundos", 60));

        _cts = new CancellationTokenSource();
        _bucle = EjecutarBucleAsync(intervalo, _cts.Token);
        return Task.CompletedTask;
    }

    public override async Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_cts is null) return;

        await _cts.CancelAsync();
        try
        {
            if (_bucle is not null) await _bucle;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task EjecutarBucleAsync(TimeSpan intervalo, CancellationToken cancellationToken)
    {
        using var temporizador = new PeriodicTimer(intervalo);
        try
        {
            while (await temporizador.WaitForNextTickAsync(cancellationToken))
                await RevalidarAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Circuito cerrado — nada que revalidar ya.
        }
    }

    private async Task RevalidarAsync(CancellationToken cancellationToken)
    {
        if (clienteActivoSeleccionado.TenantIdSeleccionado is not { } tenantSeleccionado)
            return; // Sin Workspace operativo derivado activo, nada que revalidar.

        bool sigueAutorizado;
        try
        {
            sigueAutorizado = await RevalidacionClienteActivoMiddleware.SigueAutorizadoAsync(
                clienteActivoSeleccionado, currentUserService, dbContext, operacionesContext, sesionPrivilegiadaActual,
                tenantSeleccionado, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Mejor esfuerzo: un fallo transitorio (p. ej. de base de datos)
            // no debe expulsar a un usuario legítimo en cada intento fallido.
            // La petición HTTP siguiente, y el próximo tick de este mismo
            // temporizador, vuelven a intentarlo — el middleware sigue siendo
            // el backstop que sí falla cerrado ante un token inválido.
            logger.LogWarning(ex, "No se pudo revalidar el Workspace operativo derivado del circuito; se reintentará en el próximo ciclo.");
            return;
        }

        if (!sigueAutorizado && clienteActivoSeleccionado is ClienteActivoSeleccionado seleccion)
            seleccion.Invalidar();
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }
}
