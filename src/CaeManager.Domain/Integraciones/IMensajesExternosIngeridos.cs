namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Puerto de la recuperación del hueco de correo para saber qué mensajes externos ya están ingeridos en una
/// conexión, sin depender del repositorio de conversaciones (ADR-013: las aristas del Núcleo hacia Outbound solo
/// bajan). Lo implementa el repositorio de conversaciones, con el mismo criterio que
/// <c>IConversacionRepository.ExisteMensajeExternoAsync</c>: por conexión, no por Tenant entero.
/// </summary>
public interface IMensajesExternosIngeridos
{
    /// <summary>
    /// De <paramref name="mensajeExternoIds"/>, los que ya están ingeridos en una conversación de la conexión
    /// <paramref name="conexionIntegracionId"/>, comparados por ordinal.
    /// </summary>
    Task<IReadOnlySet<string>> ObtenerMensajesExternosExistentesAsync(
        Guid conexionIntegracionId, IReadOnlyCollection<string> mensajeExternoIds, CancellationToken cancellationToken = default);
}
