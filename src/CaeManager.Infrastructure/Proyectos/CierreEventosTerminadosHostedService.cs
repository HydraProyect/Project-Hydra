using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Proyectos;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Proyectos;

/// <summary>
/// Cierra los Proyectos de tipo Evento cuya fecha de fin prevista ya pasó (decisión de producto 2026-10-16): un evento
/// no se queda abierto mostrando «fin previsto superado hace N días» ni contando como abierto en listados, filtros o
/// avisos. Cada evento terminado se cierra con <see cref="Proyecto.CerrarEventoTerminado"/>, que fija
/// <c>FechaCierreReal</c> = <c>FechaFinPrevista</c> (el día en que terminó, no el del pase). Las obras no se tocan.
/// Sus participantes vigentes (<see cref="ProyectoTecnico"/> sin <c>FechaBaja</c>) se dan de baja con
/// <c>FechaBaja</c> = <c>FechaFinPrevista</c> (o su alta, si entraron después), igual que la siembra: un evento terminado
/// no sigue mostrando «N técnicos activos» (decisión de la coordinadora, 2026-10-16, en línea con «sin ruido»). Al
/// reabrir no se reactivan solos: se vuelven a asignar.
///
/// <para>
/// <b>Cierre persistido y no derivado.</b> <c>FechaCierreReal</c> es la única marca de abierto/cerrado que leen el filtro
/// del listado, la exportación, la ficha 360, la facturación y los avisos; un cierre calculado obligaría a repetir la
/// regla del evento en cada una de esas consultas. Lo que sí sigue mirando la fecha de fin es
/// <see cref="VigenciaEvento.SinTerminarEn"/> (voluntarios, requisitos de evento, Trabajadores del Centro) y
/// <c>PlazoProyecto.CierreEfectivo</c> en la pantalla, para cubrir el hueco entre la medianoche y el siguiente pase.
/// </para>
///
/// <para>
/// <b>Auditado como Sistema.</b> El ámbito <see cref="AmbitoActorAuditoria.EstablecerSistema"/> se declara en el punto de
/// entrada, igual que en el resto de procesos programados: cada cierre deja su registro en la auditoría con
/// <c>TipoActor = Sistema</c>. Reabrir el evento es una acción de persona (<c>ReabrirProyectoCommand</c>), que exige una
/// fecha de fin de hoy o posterior para no volver a cerrarse en el pase siguiente.
/// </para>
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo mecanismo que <c>ExpiracionAsignacionesHostedService</c>: recorre
/// todos los Tenants (también los suspendidos: la fecha de un evento no deja de pasar porque su Tenant lo esté) y abre un
/// <see cref="AmbitoTenantExplicito"/> y un <c>CreateScope()</c> por cada uno. La conexión es <c>cae_app_runtime</c> y la
/// RLS de <c>Proyectos</c> solo deja ver y escribir las filas de ese Tenant; nunca una identidad que se salte la RLS.
/// </para>
///
/// Envuelto en elección de líder: dos réplicas cerrando la misma fila chocarían contra el token de concurrencia.
/// </summary>
public class CierreEventosTerminadosHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<CierreEventosTerminadosHostedService> logger)
    : BackgroundService
{
    /// <summary>
    /// Una hora, como la expiración de asignaciones: la fecha de fin de un evento se fija en días, así que el evento queda
    /// cerrado como mucho una hora después de la medianoche (día de negocio, <see cref="DiaDeNegocio"/>).
    /// </summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // P41c: todo lo que hace este servicio de fondo lo hace la propia plataforma (ver también ProcesarAsync).
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await EjecutarCicloAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await EjecutarCicloAsync(stoppingToken);
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync(
                "cierre-eventos-terminados", ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de cierre de eventos terminados.");
        }
    }

    /// <summary>
    /// Un ciclo sobre todos los Tenants, uno a uno. Interno para que el test bajo <c>cae_app_runtime</c> ejercite este
    /// mismo camino, interceptores incluidos, sin depender de la elección de líder ni del temporizador.
    /// </summary>
    internal async Task ProcesarAsync(CancellationToken stoppingToken)
    {
        // Se repite aquí, en la entrada de cada ciclo, además de en ExecuteAsync: así el test bajo cae_app_runtime, que
        // llama a este método sin pasar por ExecuteAsync, observa también el actor Sistema con el que queda auditado.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        var hoy = DiaDeNegocio.Hoy();

        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado por Tenant: un fallo (p. ej. un conflicto de concurrencia con una edición simultánea) no deja sin
            // cerrar los eventos del resto; el evento que falló se reintenta en el pase siguiente.
            try
            {
                await ProcesarTenantPropietarioAsync(tenantId, hoy, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el cierre de eventos terminados del Tenant propietario {TenantId}; se continúa con el resto.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarTenantPropietarioAsync(Guid tenantId, DateOnly hoy, CancellationToken stoppingToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        using var ambito = ambitoFactory.CreateScope();
        var dbContext = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var pendientes = await dbContext.Proyectos
            .Where(VigenciaEvento.PendienteDeCierreEn(hoy))
            .ToListAsync(stoppingToken);

        var cerrados = pendientes.Where(evento => evento.CerrarEventoTerminado(hoy)).ToList();
        if (cerrados.Count == 0)
            return;

        var finPorEvento = cerrados.ToDictionary(evento => evento.Id, evento => evento.FechaFinPrevista!.Value);
        var idsCerrados = finPorEvento.Keys.ToList();
        var participantes = await dbContext.ProyectosTecnicos
            .Where(participante => idsCerrados.Contains(participante.ProyectoId) && participante.FechaBaja == null)
            .ToListAsync(stoppingToken);
        foreach (var participante in participantes)
        {
            var fin = finPorEvento[participante.ProyectoId];
            participante.DarDeBaja(fin < participante.FechaAlta ? participante.FechaAlta : fin);
        }

        await dbContext.SaveChangesAsync(stoppingToken);
        logger.LogInformation(
            "Cierre de eventos terminados: {Cerrados} cerrados y {Participantes} participantes dados de baja en el Tenant propietario {TenantId}.",
            cerrados.Count, participantes.Count, tenantId);
    }
}
