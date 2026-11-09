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

    /// <summary>
    /// Corre al tomar el cerrojo, antes del trabajo: lo que otro proceso cambió entre la selección de la conexión y su
    /// comprobación (la renovación y la ingesta no toman este cerrojo).
    /// </summary>
    public Action<Guid>? AlTomar { get; set; }

    /// <summary>Corre al soltar el cerrojo, cuando el trabajo termina (también si lanza).</summary>
    public Action<Guid>? AlSalir { get; set; }

    public async Task<bool> IntentarEjecutarAsync(Guid conexionId, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
    {
        Pedidos.Add(conexionId);
        if (Ocupadas.Contains(conexionId))
            return false;

        AlTomar?.Invoke(conexionId);
        try
        {
            await trabajo(cancellationToken);
        }
        finally
        {
            AlSalir?.Invoke(conexionId);
        }

        return true;
    }
}