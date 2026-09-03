using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Tenants;
using CaeManager.Application.VigilanciaNormativa;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.VigilanciaNormativa;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Configuracion;
using CaeManager.Infrastructure.Coordinacion;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.VigilanciaNormativa;

/// <summary>
/// Tramo 1 bis del MVP-1 de formatos (corte mínimo, confirmado 2026-08-14):
/// consulta el sumario diario del BOE y abre un <see cref="AvisoRevisionNormativa"/>
/// cuando el título de una publicación toca una norma de <see cref="NormasVigiladas"/>.
/// Sin interpretar nada — ni una IA ni una regla decide si la publicación
/// afecta de verdad al catálogo, eso lo hace una persona al revisar el
/// aviso. Global, no por tenant (mismo tratamiento que
/// <see cref="Coordinacion.EleccionLiderPostgresService"/> exige para
/// evitar procesos duplicados entre réplicas) — el BOE es el mismo para
/// todos los tenants y el destinatario del aviso es quien mantiene
/// CATALOGO_FORMATOS_PRL.md, no un cliente.
/// </summary>
public class VigilanciaNormativaBoeHostedService(
    IServiceScopeFactory ambitoFactory,
    IEleccionLiderService eleccionLider,
    ILogger<VigilanciaNormativaBoeHostedService> logger)
    : BackgroundService
{
    // El BOE publica de lunes a viernes; dos sondeos al día cubren con
    // margen el caso de que el sumario del día todavía no estuviera
    // publicado en el primer intento, sin necesidad de reintentos propios.
    private static readonly TimeSpan IntervaloSondeo = TimeSpan.FromHours(12);

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
            var publicacionesEvaluadas = 0;
            var fueLider = await eleccionLider.IntentarEjecutarComoLiderAsync(
                "vigilancia-normativa-boe", async ct => publicacionesEvaluadas = await ProcesarAsync(ct), stoppingToken);

            // Hallazgo de revisión Codex (REC-084/REC-126): si otra réplica ya
            // tiene el liderazgo, el callback no corre y publicacionesEvaluadas
            // se queda en 0 — registrar "exitosa" aquí escribiría una ejecución
            // que no ocurrió, con un recuento falso, en la pantalla de todos
            // los tenants. Sin liderazgo, no hay nada que registrar.
            if (fueLider)
                await RegistrarEjecucionEnTodosLosTenantsAsync(exitosa: true, stoppingToken, elementosEvaluados: publicacionesEvaluadas);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de vigilancia normativa del BOE.");
            await RegistrarEjecucionEnTodosLosTenantsAsync(exitosa: false, stoppingToken, mensajeError: ex.Message);
        }
    }

    /// <summary>
    /// Este trabajo es global (mismo BOE para todos los tenants, ver
    /// comentario de clase) — no tiene un Activo por tenant que comprobar,
    /// pero la pantalla de Automatizaciones SÍ es por tenant, así que se
    /// deja una marca de "última ejecución" en cada tenant activo para que
    /// cada uno vea un dato real en vez de "—" permanente.
    /// </summary>
    private async Task RegistrarEjecucionEnTodosLosTenantsAsync(
        bool exitosa, CancellationToken stoppingToken, int? elementosEvaluados = null, string? mensajeError = null)
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
            using var ambito = ambitoFactory.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);
            var registro = ambito.ServiceProvider.GetRequiredService<IRegistroAutomatizacionesService>();
            await registro.RegistrarEjecucionAsync(
                CatalogoAutomatizaciones.VigilanciaNormativaBoe, exitosa, stoppingToken,
                mensajeError: mensajeError, elementosEvaluados: elementosEvaluados);
        }
    }

    /// <returns>Cuántas publicaciones traía el sumario del día.</returns>
    private async Task<int> ProcesarAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();

        var cliente = ambito.ServiceProvider.GetRequiredService<IBoeSumarioClient>();
        var repositorio = ambito.ServiceProvider.GetRequiredService<IAvisoRevisionNormativaRepository>();
        var unitOfWork = ambito.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var publicaciones = await cliente.ObtenerSumarioAsync(hoy, stoppingToken);
        if (publicaciones.Count == 0) return 0;

        var huboNuevos = false;

        foreach (var publicacion in publicaciones)
        {
            var normaCoincidente = NormasVigiladas.Detectar(publicacion.Titulo);
            if (normaCoincidente is null) continue;

            if (await repositorio.ExisteParaPublicacionAsync(publicacion.Identificador, stoppingToken)) continue;

            repositorio.Agregar(new AvisoRevisionNormativa(
                publicacion.Identificador, hoy, publicacion.Titulo, publicacion.UrlHtml,
                normaCoincidente, DateTime.UtcNow));
            huboNuevos = true;
        }

        if (huboNuevos)
            await unitOfWork.SaveChangesAsync(stoppingToken);

        return publicaciones.Count;
    }
}
