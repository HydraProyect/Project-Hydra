using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Comunicaciones;

/// <summary>Reintenta únicamente los avisos persistentes pendientes. No envía correo ni atribuye rendimiento a personas.</summary>
public class AvisosConversacionesCriticasHostedService(IServiceScopeFactory scopes, IEleccionLiderService lider,
    ILogger<AvisosConversacionesCriticasHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        using var reloj = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await lider.IntentarEjecutarComoLiderAsync("avisos-conversaciones-criticas", ProcesarAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Falló el reintento de avisos de conversaciones Críticas."); }
        } while (await reloj.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task ProcesarAsync(CancellationToken cancellationToken)
    {
        using var actor = AmbitoActorAuditoria.EstablecerSistema();
        List<Guid> tenants;
        using (var scope = scopes.CreateScope())
            tenants = await scope.ServiceProvider.GetRequiredService<ITenantsQueryContext>().Tenants
                .Select(t => t.Id).ToListAsync(cancellationToken);
        foreach (var tenantId in tenants)
        {
            try
            {
                using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
                List<Guid> pendientes;
                using (var scope = scopes.CreateScope())
                    pendientes = await scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>().Conversaciones
                        .Where(c => c.AvisoCriticaPendienteId != null).Select(c => c.Id).ToListAsync(cancellationToken);
                foreach (var id in pendientes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        using var scope = scopes.CreateScope();
                        var db = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                        var conversacion = await db.Conversaciones.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
                        if (conversacion?.AvisoCriticaPendienteId is not { } eventoId) continue;
                        var contexto = scope.ServiceProvider.GetRequiredService<IContextoOperativoConversacionService>();
                        var operacion = await contexto.ResolverAsync(tenantId, conversacion.ClienteId, conversacion.EmpresaId, cancellationToken);
                        if (operacion is null) continue;
                        await contexto.AvisarCriticaAsync(operacion, eventoId, cancellationToken);
                        conversacion.ConfirmarAvisoCritica(eventoId);
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "El aviso de la conversación {ConversacionId} sigue pendiente.", id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "No se pudieron reintentar los avisos del Tenant propietario {TenantId}.", tenantId);
            }
        }
    }
}
