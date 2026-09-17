using CaeManager.Application.Common;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Doble mínimo de <see cref="IAutorizacionEscrituraEfectiva"/> para los tests
/// de integración de importación que no ejercitan PD-A3: devuelve el valor
/// fijo con el que se construye, sin consultar ninguna sesión privilegiada.
/// La rama de Aprovisionamiento de verdad la cubre
/// <c>EscrituraAprovisionamientoEnLaCapaDeDatosTests</c>.
/// </summary>
public class AutorizacionEscrituraEfectivaFalsa(bool esActoDeAdministrador = true) : IAutorizacionEscrituraEfectiva
{
    public Task<bool> EsActoDeAdministradorAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(esActoDeAdministrador);
}
