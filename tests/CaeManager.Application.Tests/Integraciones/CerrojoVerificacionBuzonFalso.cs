using CaeManager.Application.Integraciones;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Cerrojo de verificación en memoria: ejecuta el trabajo salvo para las conexiones marcadas como ocupadas, que simulan
/// otra verificación en curso (en otra réplica, en el ciclo diario o en Reactivar).
/// </summary>
public sealed class CerrojoVerificacionBuzonFalso : ICerrojoVerificacionBuzon
{
    public HashSet<Guid> Ocupadas { get; } = [];

    public List<Guid> Pedidos { get; } = [];

    public async Task<bool> IntentarEjecutarAsync(Guid conexionId, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
    {
        Pedidos.Add(conexionId);
        if (Ocupadas.Contains(conexionId))
            return false;

        await trabajo(cancellationToken);
        return true;
    }
}