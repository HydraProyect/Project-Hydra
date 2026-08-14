using CaeManager.Application.Common;
using CaeManager.Application.VigilanciaNormativa;
using CaeManager.Domain.VigilanciaNormativa;
using CaeManager.Infrastructure.Coordinacion;
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
            await eleccionLider.IntentarEjecutarComoLiderAsync("vigilancia-normativa-boe", ProcesarAsync, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falló un ciclo de vigilancia normativa del BOE.");
        }
    }

    private async Task ProcesarAsync(CancellationToken stoppingToken)
    {
        using var ambito = ambitoFactory.CreateScope();

        var cliente = ambito.ServiceProvider.GetRequiredService<IBoeSumarioClient>();
        var repositorio = ambito.ServiceProvider.GetRequiredService<IAvisoRevisionNormativaRepository>();
        var unitOfWork = ambito.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var publicaciones = await cliente.ObtenerSumarioAsync(hoy, stoppingToken);
        if (publicaciones.Count == 0) return;

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
    }
}
