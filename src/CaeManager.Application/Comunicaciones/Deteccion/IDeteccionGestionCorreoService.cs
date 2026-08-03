using CaeManager.Domain.Common;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Proveedor de IA de texto que decide si el cuerpo de un correo entrante
/// parece pedir la actualización/renovación de un documento de un
/// Trabajador (p. ej. EPI) y, si lo parece, a qué Trabajador y a qué
/// TipoDocumento se refiere. Mismo criterio de "sugerencia, nunca
/// automática" que <see cref="IDeteccionVisitaCorreoService"/> — el llamador
/// (<c>ISugerenciaGestionCorreoService</c>) decide qué hacer con el
/// resultado, esta interfaz solo clasifica/extrae.
/// </summary>
public interface IDeteccionGestionCorreoService
{
    Task<Result<DeteccionGestionCorreoDto>> DetectarAsync(
        string cuerpoMensaje,
        IReadOnlyList<TrabajadorCandidatoGestionDto> trabajadoresDisponibles,
        IReadOnlyList<TipoDocumentoCandidatoGestionDto> tiposDocumentoDisponibles,
        CancellationToken cancellationToken = default);
}

public record TrabajadorCandidatoGestionDto(Guid Id, string NombreCompleto, string? Dni);

public record TipoDocumentoCandidatoGestionDto(Guid Id, string Nombre);

/// <summary>
/// <paramref name="TrabajadorId"/>/<paramref name="TipoDocumentoId"/>,
/// cuando no son null, siempre son uno de los Id de las listas de
/// candidatos pasadas a la petición — el proveedor concreto descarta
/// cualquier Id que el modelo alucine fuera de esas listas (ver
/// AnthropicDeteccionGestionCorreoService).
/// </summary>
public record DeteccionGestionCorreoDto(
    bool EsActualizacionDocumento, Guid? TrabajadorId, Guid? TipoDocumentoId, string? Resumen);
