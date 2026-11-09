namespace CaeManager.Application.Integraciones;

/// <summary>
/// Exclusión entre verificaciones de la identidad de una misma conexión: como mucho una a la vez, venga del ciclo diario,
/// del reintento o de Reactivar, en cualquier réplica o ranura del despliegue azul/verde. No bloquea: si otra ya está en
/// curso, no ejecuta nada y devuelve false. La implementación (Infrastructure) es un advisory lock de sesión de
/// PostgreSQL por conexión, el mismo mecanismo que la elección de líder: se libera solo si el proceso cae.
/// </summary>
public interface ICerrojoVerificacionBuzon
{
    Task<bool> IntentarEjecutarAsync(Guid conexionId, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken);
}
