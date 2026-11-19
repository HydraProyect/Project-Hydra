namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Una entidad que declara por sí misma qué propiedades suyas no deben llegar en claro al historial de auditoría
/// (ADR-013, mecanismo del Núcleo: la marca vive aquí, en el Núcleo, y la implementa el módulo propietario de la entidad;
/// así <c>AuditoriaInterceptor</c> enmascara sin nombrar tipos de Inbound ni de ningún otro módulo). El interceptor
/// la une a su tabla <c>PropiedadesSensiblesPorTipo</c>: enmascara el mismo conjunto en DatosAntes y DatosDespues.
/// Implementación explícita de la interfaz, para que EF no la tome por una propiedad persistible.
/// </summary>
public interface IPropiedadesSensiblesAuditoria
{
    /// <summary>Los nombres de propiedad (<c>nameof</c>) que se enmascaran en el historial.</summary>
    IReadOnlyCollection<string> PropiedadesSensiblesAuditoria { get; }
}
