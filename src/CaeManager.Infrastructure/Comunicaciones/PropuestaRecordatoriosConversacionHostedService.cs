using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>
/// Propone un recordatorio en cada conversación de correo que lleva
/// <see cref="PropuestaRecordatorioConversacion.DiasLaborablesDeEspera"/> días laborables en
/// <see cref="EstadoConversacion.EsperandoRespuestaExterna"/> (decisión de producto del 2026-10-17). <b>Solo propone</b>: no
/// envía nada; el Gestor CAE aprueba o descarta en la pantalla de Comunicaciones. Y descarta solas las propuestas Pendientes
/// cuya espera ya terminó (llegó la respuesta, o alguien cambió el estado).
///
/// <para>
/// <b>Calendario.</b> Los días laborables son los del calendario del Operador CAE que opera la conversación
/// (<see cref="PlazosConversacionService.ObtenerCalendarioAsync"/>, el mismo que el SLA). Si el Operador CAE no se puede
/// resolver, se usa el calendario por defecto, sin festivos: la espera no se queda sin proponer por eso.
/// </para>
///
/// <para>
/// <b>Un Tenant propietario cada vez</b>, con el mismo mecanismo que <see cref="CierreConversacionesResueltasHostedService"/>:
/// recorre todos los Tenants (también los suspendidos) y abre un <see cref="AmbitoTenantExplicito"/> y un <c>CreateScope()</c>
/// por cada uno. La conexión es <c>cae_app_runtime</c> y la RLS de <c>Conversaciones</c> y
/// <c>PropuestasRecordatorioConversacion</c> solo deja ver y escribir las filas de ese Tenant. Auditado como Sistema. Envuelto
/// en elección de líder: dos réplicas proponiendo a la vez chocarían contra el índice único de la espera.
/// </para>
/// </summary>
public class PropuestaRecordatoriosConversacionHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<PropuestaRecordatoriosConversacionHostedService> logger)
    : BackgroundService
{
    /// <summary>Una hora: el plazo se mide en días laborables, así que la propuesta llega como mucho una hora tarde.</summary>
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                "propuesta-recordatorios-conversacion", ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de propuesta de recordatorios de conversación.");
        }
    }

    /// <summary>
    /// Un ciclo sobre todos los Tenants, uno a uno. Interno para que un test bajo <c>cae_app_runtime</c> ejercite este mismo
    /// camino, interceptores incluidos, sin depender de la elección de líder ni del temporizador.
    /// </summary>
    internal async Task ProcesarAsync(CancellationToken stoppingToken) =>
        await ProcesarAsync(DateTime.UtcNow, stoppingToken);

    internal async Task ProcesarAsync(DateTime ahoraUtc, CancellationToken stoppingToken)
    {
        // Se repite en la entrada de cada ciclo, además de en ExecuteAsync, para que quien llame a este método sin pasar
        // por ExecuteAsync observe también el actor Sistema.
        using var ambitoActor = AmbitoActorAuditoria.EstablecerSistema();

        List<Guid> tenants;
        using (var ambito = ambitoFactory.CreateScope())
        {
            tenants = await ambito.ServiceProvider.GetRequiredService<ITenantsQueryContext>()
                .Tenants.Select(t => t.Id)
                .ToListAsync(stoppingToken);
        }

        foreach (var tenantId in tenants)
        {
            stoppingToken.ThrowIfCancellationRequested();

            // Aislado por Tenant: un fallo en uno no deja sin proponer las del resto; se reintenta en el pase siguiente.
            try
            {
                await ProcesarTenantPropietarioAsync(tenantId, ahoraUtc, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló la propuesta de recordatorios del Tenant propietario {TenantId}; se continúa con el resto.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarTenantPropietarioAsync(Guid tenantId, DateTime ahoraUtc, CancellationToken stoppingToken)
    {
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        using var ambito = ambitoFactory.CreateScope();
        var dbContext = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var plazos = ambito.ServiceProvider.GetRequiredService<PlazosConversacionService>();

        // Los descartes se guardan antes de proponer: si la conversación salió de la espera y volvió a entrar, la
        // Pendiente vieja tiene que dejar de serlo antes de insertar la nueva, o el índice único parcial de Pendiente
        // por conversación rechazaría el lote entero según el orden en que EF emitiera los comandos.
        var descartadas = await DescartarLasQueYaNoAplicanAsync(dbContext, ahoraUtc, stoppingToken);
        if (descartadas > 0)
            await dbContext.SaveChangesAsync(stoppingToken);

        // Dos días laborables nunca son menos de dos naturales: el filtro grueso va en SQL y el fino, con el calendario.
        // Con buzón conectado, el envío sale por /reply sobre el último entrante del hilo (ResponderConversacionCommand):
        // un hilo que abrió el propio Gestor CAE y nadie ha contestado aún no tiene a qué responder, y proponer allí
        // un recordatorio que no se puede enviar dejaría una tarjeta imposible de aprobar.
        var limiteGrueso = ahoraUtc.AddDays(-PropuestaRecordatorioConversacion.DiasLaborablesDeEspera);
        var candidatas = await dbContext.Conversaciones
            .AsNoTracking()
            .Where(c => c.Estado == EstadoConversacion.EsperandoRespuestaExterna
                        && c.Canal == CanalConversacion.Correo
                        && c.EstadoDesdeUtc <= limiteGrueso
                        && (c.ConexionIntegracionId == null
                            || c.Mensajes.Any(m => m.Direccion == DireccionMensaje.Entrante && m.MensajeExternoId != null))
                        && !dbContext.PropuestasRecordatorioConversacion.Any(
                            p => p.ConversacionId == c.Id && p.EsperaDesdeUtc == c.EstadoDesdeUtc))
            .ToListAsync(stoppingToken);

        var propuestas = 0;
        foreach (var conversacion in candidatas)
        {
            var calendario = await plazos.ObtenerCalendarioAsync(conversacion, stoppingToken)
                             ?? new CalendarioLaboralOperadorCae([]);
            if (PropuestaRecordatorioConversacion.ProponerSiCorresponde(conversacion, ahoraUtc, calendario) is not { } propuesta)
                continue;

            dbContext.PropuestasRecordatorioConversacion.Add(propuesta);
            propuestas++;
        }

        if (propuestas == 0 && descartadas == 0)
            return;

        if (propuestas > 0)
            await dbContext.SaveChangesAsync(stoppingToken);
        logger.LogInformation(
            "Recordatorios de conversación: {Propuestas} propuestos y {Descartadas} descartados en el Tenant propietario {TenantId}.",
            propuestas, descartadas, tenantId);
    }

    /// <summary>
    /// Las propuestas Pendientes cuya conversación ya no está en la misma espera (o ya no existe) se descartan: la respuesta
    /// llegó, o alguien cambió el estado. Las deja modificadas en el contexto; el llamador guarda.
    /// </summary>
    private static async Task<int> DescartarLasQueYaNoAplicanAsync(
        CaeManagerDbContext dbContext, DateTime ahoraUtc, CancellationToken stoppingToken)
    {
        var pendientes = await dbContext.PropuestasRecordatorioConversacion
            .Where(p => p.Estado == EstadoPropuestaRecordatorio.Pendiente)
            .ToListAsync(stoppingToken);
        if (pendientes.Count == 0)
            return 0;

        var ids = pendientes.Select(p => p.ConversacionId).Distinct().ToList();
        var estados = await dbContext.Conversaciones
            .Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Estado, c.EstadoDesdeUtc })
            .ToDictionaryAsync(c => c.Id, stoppingToken);

        var descartadas = 0;
        foreach (var propuesta in pendientes)
        {
            if (estados.TryGetValue(propuesta.ConversacionId, out var estado)
                && propuesta.SigueVigentePara(estado.Estado, estado.EstadoDesdeUtc))
                continue;

            if (propuesta.Descartar(ahoraUtc).EsExitoso)
                descartadas++;
        }

        return descartadas;
    }
}
