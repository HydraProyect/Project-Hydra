using CaeManager.Application.Importacion;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// <c>/salud</c> de la plantilla CAE completa (M-43): sin hojas de Trabajadores configuradas es Degraded (200),
/// nunca Unhealthy, porque un entorno que no usa la plantilla tiene que poder servir y arrancar.
/// </summary>
public sealed class PlantillaCaeCompletaHealthCheck(IPlantillaCaeCompletaConfigurada plantilla) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(plantilla.EstaConfigurada
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Degraded(
                $"La plantilla CAE completa no tiene hojas de Trabajadores configuradas ({OpcionesPlantillaCaeCompleta.Seccion}): no se puede importar con ella."));
}
