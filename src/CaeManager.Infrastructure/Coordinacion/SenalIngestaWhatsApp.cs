using CaeManager.Application.Integraciones;

namespace CaeManager.Infrastructure.Coordinacion;

/// <summary>
/// Implementación en proceso de <see cref="ISenalIngestaWhatsApp"/> —
/// singleton. Un <see cref="SemaphoreSlim"/> con capacidad 1: señales
/// repetidas mientras el consumidor trabaja colapsan en una sola (no hace
/// falta contar — el consumidor drena la cola entera en cada pasada).
/// </summary>
public class SenalIngestaWhatsApp : ISenalIngestaWhatsApp
{
    private readonly SemaphoreSlim _semaforo = new(0, 1);

    public void Despertar()
    {
        try
        {
            _semaforo.Release();
        }
        catch (SemaphoreFullException)
        {
            // Ya había una señal pendiente — colapsar es exactamente lo que queremos.
        }
    }

    public Task EsperarAsync(TimeSpan maximo, CancellationToken cancellationToken) =>
        _semaforo.WaitAsync(maximo, cancellationToken);
}
