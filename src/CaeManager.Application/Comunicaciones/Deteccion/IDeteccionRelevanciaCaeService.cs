using CaeManager.Domain.Common;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Proveedor de IA de texto que decide si una conversación con un Cliente ya tiene alguna gestión
/// CAE real que hacer (alta de Centro/Trabajador, documentación requerida, etc.) o si todavía es
/// solo contexto de negocio — el cliente copia al gestor desde el inicio de una negociación
/// comercial, antes de que exista ninguna gestión. Mismo criterio de "clasifica, nunca decide" que
/// <see cref="IDeteccionVisitaCorreoService"/>/<see cref="IDeteccionGestionCorreoService"/> — el
/// llamador (<c>RelevanciaCaeService</c>) decide qué hacer con el resultado.
/// </summary>
public interface IDeteccionRelevanciaCaeService
{
    Task<Result<DeteccionRelevanciaCaeDto>> DetectarAsync(string cuerpoConversacion, CancellationToken cancellationToken = default);
}

public record DeteccionRelevanciaCaeDto(bool EsAccionableCae, string? Resumen, int Confianza);
