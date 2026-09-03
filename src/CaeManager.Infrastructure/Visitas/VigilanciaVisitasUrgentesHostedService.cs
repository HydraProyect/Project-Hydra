using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Identity;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Visitas;

/// <summary>
/// Fase F: avisa por hora (no por correo en v1 — solo la campana,
/// <see cref="NotificacionUsuario"/>) cuando un tenant tiene gestiones
/// urgentes de visita pendientes (Visitas dentro de la ventana mínima de
/// validación, o sugerencias de visita sorpresa sin confirmar) — mismo
/// patrón de elección de líder que <see cref="Alertas.EnvioAlertasVencimientoHostedService"/>,
/// reutilizando el cálculo ya existente de <see cref="ObtenerBandejaGestorQuery"/>
/// en vez de reimplementar el criterio de urgencia aquí.
///
/// Destinatarios: Administrador y DireccionCae, mismo criterio (y misma
/// limitación documentada) que el resumen de alertas por correo — atribuir
/// una gestión urgente a "el" Gestor CAE responsable exigiría resolver
/// Trabajador/Centro → Empresa/Subcontrata → Cliente, y esa relación puede
/// ser N:M (ver el comentario de EnvioAlertasVencimientoHostedService).
/// Sin dedupe entre avisos: es deliberadamente un aviso por hora mientras la
/// condición se mantenga, no una notificación única por gestión — el propio
/// nombre del pedido ("avisos por hora") lo pide así.
/// </summary>
public class VigilanciaVisitasUrgentesHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<VigilanciaVisitasUrgentesHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await AvisarATodosLosTenantsAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await AvisarATodosLosTenantsAsync(stoppingToken);
    }

    private async Task AvisarATodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("vigilancia-visitas-urgentes", ProcesarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de vigilancia de gestiones urgentes de visita.");
        }
    }

    private async Task ProcesarTodosLosTenantsAsync(CancellationToken stoppingToken)
    {
        List<Guid> tenantsActivos;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenantsActivos = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Where(t => t.Estado == EstadoTenant.Activo)
                .Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenantsActivos)
        {
            stoppingToken.ThrowIfCancellationRequested();
            await ProcesarTenantAsync(tenantId, stoppingToken);
        }
    }

    private async Task ProcesarTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);

        var registroAutomatizaciones = ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
        if (!await registroAutomatizaciones.EstaActivoAsync(CatalogoAutomatizaciones.VigilanciaVisitasUrgentes, stoppingToken))
            return;

        try
        {
            var gestionesUrgentes = await AvisarTenantAsync(ambito, stoppingToken);
            await registroAutomatizaciones.RegistrarEjecucionAsync(
                CatalogoAutomatizaciones.VigilanciaVisitasUrgentes, exitosa: true, stoppingToken,
                elementosEvaluados: gestionesUrgentes);
        }
        catch (Exception ex)
        {
            await registroAutomatizaciones.RegistrarEjecucionAsync(
                CatalogoAutomatizaciones.VigilanciaVisitasUrgentes, exitosa: false, stoppingToken, mensajeError: ex.Message);
            throw;
        }
    }

    /// <returns>Cuántas gestiones urgentes había, avisadas o no.</returns>
    private async Task<int> AvisarTenantAsync(IServiceScope ambito, CancellationToken stoppingToken)
    {
        var mediator = ambito.ServiceProvider.GetRequiredService<IMediator>();
        var items = await mediator.Send(new ObtenerBandejaGestorQuery(), stoppingToken);

        var gestionesUrgentes = items.Count(i => i.Tipo is TipoItemBandeja.VisitaUrgente or TipoItemBandeja.SugerenciaVisitaUrgente);
        if (gestionesUrgentes == 0) return 0;

        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();
        var administradores = await directorio.ObtenerVisiblesEnRolAsync(Roles.Administrador, stoppingToken);
        var direccionCae = await directorio.ObtenerVisiblesEnRolAsync(Roles.DireccionCae, stoppingToken);

        var destinatarios = administradores.Concat(direccionCae).DistinctBy(u => u.Id).ToList();
        if (destinatarios.Count == 0) return gestionesUrgentes;

        var repositorio = ambito.ServiceProvider.GetRequiredService<INotificacionUsuarioRepository>();
        var mensaje = gestionesUrgentes == 1
            ? "Hay 1 gestión urgente de visita pendiente (ventana mínima de validación)."
            : $"Hay {gestionesUrgentes} gestiones urgentes de visita pendientes (ventana mínima de validación).";

        foreach (var destinatario in destinatarios)
            repositorio.Agregar(new NotificacionUsuario(destinatario.Id, "Gestiones urgentes de visita", mensaje, "/bandeja", "Ver bandeja"));

        await ambito.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(stoppingToken);

        return gestionesUrgentes;
    }
}
