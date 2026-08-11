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
public record ItemDeteccionGestionDto(Guid? TrabajadorId, Guid? TipoDocumentoId, int ConfianzaTrabajador, int ConfianzaTipoDocumento);

/// <summary>
/// Algunas plataformas externas solo notifican un resumen agregado ("3 pendientes, 1 vencido, 0
/// rechazados") sin decir de quién ni de qué documento — no hay ítems que extraer, solo estas tres
/// cifras (ronda de reducción de ruido en Comunicaciones, patrón de "plataformas de solo resumen").
/// </summary>
public record ResumenAgregadoGestionDto(int Pendientes, int Vencidos, int Rechazados);

/// <summary>
/// Un correo no siempre trata un único Trabajador/TipoDocumento — una notificación en bloque
/// puede listar varios a la vez (ronda de reducción de ruido en Comunicaciones), de ahí
/// <paramref name="Items"/>. <paramref name="Resumen"/>/<paramref name="Confianza"/> son del
/// mensaje completo; la certeza de cada ítem vive en su propio <see cref="ItemDeteccionGestionDto"/>.
/// <paramref name="ResumenAgregado"/> solo se rellena cuando el correo es de una plataforma de
/// solo resumen — nunca a la vez que <paramref name="Items"/> tiene elementos.
/// </summary>
public record DeteccionGestionCorreoDto(
    bool EsActualizacionDocumento, string? Resumen, int Confianza, IReadOnlyList<ItemDeteccionGestionDto> Items,
    ResumenAgregadoGestionDto? ResumenAgregado = null);
