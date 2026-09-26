using CaeManager.Application.Documentos.Acreditacion;

namespace CaeManager.Application.Tests;

/// <summary>
/// <see cref="IAltaAcreditacionesPlataformaService"/> que solo recoge las altas
/// que cada camino le entrega, para los tests de un handler que no ejercitan la
/// regla de acreditación en sí (esa se prueba contra el servicio real).
/// </summary>
public class AltaAcreditacionesPlataformaServiceFalso : IAltaAcreditacionesPlataformaService
{
    public List<AltasConAcreditacion> Recibidas { get; } = [];

    public Task<int> AgregarPendientesAsync(AltasConAcreditacion altas, CancellationToken cancellationToken = default)
    {
        Recibidas.Add(altas);
        return Task.FromResult(0);
    }
}
