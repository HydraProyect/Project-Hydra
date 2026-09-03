using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Retencion;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Retencion;

/// <summary>
/// Automatiza el barrido de retención (HO-084-01, REC-084, DEC-35): «Sin
/// política de retención aprobada/configurada: no se borra automáticamente.
/// Puede existir dry-run/diagnóstico. Con política aprobada y efectiva: el
/// barrido debe ejecutarse automáticamente.»
///
/// Interpretación fijada aquí, por ser la que no exige tocar ninguna
/// invariante de autorización existente: "el barrido" es la <b>detección</b>
/// (<see cref="DeteccionPurgaService"/>), nunca la destrucción. Con política
/// activa, este servicio hace exactamente lo que hoy hace el botón "Buscar"
/// manual de Retencion.razor — crea <c>SolicitudPurga</c> en
/// <c>PendienteDeRevision</c> — y dependen del mismo camino humano de
/// siempre (avisar → <c>ProgramarPurgaCommand</c>, que exige un usuario
/// autenticado y no existe en un job de fondo → <c>EjecutarPurgaCommand</c>)
/// para llegar a destruir algo. Sin política, corre en modo diagnóstico
/// (<see cref="DeteccionPurgaService.DiagnosticarAsync"/>): cuenta y no crea
/// nada. Ninguna rama de este servicio llama a <c>EjecutarPurgaCommand</c> ni
/// a <c>ProgramarPurgaCommand</c> — si el propietario del producto decide más
/// adelante que la propia ejecución también debe automatizarse sin paso
/// humano, es una decisión de autorización nueva (quién autoriza en nombre de
/// quién) que corresponde elevar, no inventar aquí.
///
/// Mismo patrón de elección de líder, sondeo diario y ámbito de tenant
/// explícito por iteración que <see cref="Alertas.EnvioAlertasVencimientoHostedService"/>
/// y <see cref="Visitas.VigilanciaVisitasUrgentesHostedService"/>. Siempre
/// registrado (sin interruptor de configuración propio, igual que
/// VigilanciaVisitasUrgentes): el diagnóstico debe poder correr aunque la
/// política esté apagada, que es justo el caso que existe para cubrir.
/// </summary>
public class RetencionHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<RetencionHostedService> logger)
    : BackgroundService
{
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var temporizador = new PeriodicTimer(IntervaloSondeo);

        await EjecutarCicloAsync(stoppingToken);

        while (await temporizador.WaitForNextTickAsync(stoppingToken))
            await EjecutarCicloAsync(stoppingToken);
    }

    private async Task EjecutarCicloAsync(CancellationToken stoppingToken)
    {
        try
        {
            await eleccionLider.IntentarEjecutarComoLiderAsync("barrido-retencion-datos", ProcesarTodosLosTenantsAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo del barrido automático de retención.");
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

            // Aislado igual que el resto de hosted services por tenant: un
            // fallo en un tenant (p. ej. una consulta que choca con RLS) no
            // debe dejar sin barrido al resto.
            try
            {
                await ProcesarTenantAsync(tenantId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Falló el barrido de retención del tenant {TenantId}; se continúa con el resto de tenants en este ciclo.",
                    tenantId);
            }
        }
    }

    private async Task ProcesarTenantAsync(Guid tenantId, CancellationToken stoppingToken)
    {
        using (var ambitoComprobacion = ambitoFactory.CreateScope())
        {
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);
            var registroComprobacion = ambitoComprobacion.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
            if (!await registroComprobacion.EstaActivoAsync(CatalogoAutomatizaciones.BarridoRetencionDatos, stoppingToken))
                return;
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        try
        {
            // Ámbito propio para la detección/diagnóstico, sin compartirlo con
            // el registro de la ejecución (hallazgo de revisión Codex,
            // REC-084/REC-126): si DetectarAsync crea la solicitud de
            // Documentos y después falla al detectar Trabajadores, esa
            // solicitud queda en el ChangeTracker de ESTE DbContext sin
            // guardar. Si RegistrarEjecucionAsync hiciera su SaveChangesAsync
            // sobre el mismo contexto, la persistiría de rebote junto al
            // registro de la ejecución fallida — una propuesta a medio crear
            // colándose bajo una ejecución marcada como "Fallida".
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            var opciones = ambito.ServiceProvider.GetRequiredService<IOptions<RetencionDatosOptions>>().Value;
            var deteccion = ambito.ServiceProvider.GetRequiredService<DeteccionPurgaService>();

            if (opciones.PoliticaAprobadaYEfectiva)
            {
                var creadas = await deteccion.DetectarAsync(hoy, stoppingToken);
                await RegistrarEjecucionAsync(tenantId, exitosa: true, stoppingToken, elementosAfectados: creadas);
            }
            else
            {
                var diagnostico = await deteccion.DiagnosticarAsync(hoy, stoppingToken);
                await RegistrarEjecucionAsync(tenantId, exitosa: true, stoppingToken, elementosEvaluados: diagnostico.Total);
            }
        }
        catch (Exception ex)
        {
            await RegistrarEjecucionAsync(tenantId, exitosa: false, stoppingToken, mensajeError: ex.Message);
            throw;
        }
    }

    private async Task RegistrarEjecucionAsync(
        Guid tenantId, bool exitosa, CancellationToken stoppingToken,
        string? mensajeError = null, int? elementosEvaluados = null, int? elementosAfectados = null)
    {
        using var ambito = ambitoFactory.CreateScope();
        using var _ = AmbitoTenantExplicito.Establecer(tenantId);
        var registro = ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
        await registro.RegistrarEjecucionAsync(
            CatalogoAutomatizaciones.BarridoRetencionDatos, exitosa, stoppingToken,
            mensajeError: mensajeError, elementosEvaluados: elementosEvaluados, elementosAfectados: elementosAfectados);
    }
}
