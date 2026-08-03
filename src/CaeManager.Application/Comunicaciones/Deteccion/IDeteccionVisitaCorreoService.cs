using CaeManager.Domain.Common;

namespace CaeManager.Application.Comunicaciones.Deteccion;

/// <summary>
/// Proveedor de IA de texto que decide si el cuerpo de un correo entrante
/// parece una solicitud de visita y, si lo parece, extrae fecha(s) y a cuál
/// de los Centros candidatos del Cliente se refiere. Mismo criterio de
/// "sugerencia, nunca automática" que <c>IExtraccionTrabajadoresIaService</c>
/// — el llamador (<see cref="ISugerenciaVisitaCorreoService"/>) decide qué
/// hacer con el resultado, esta interfaz solo clasifica/extrae.
/// </summary>
public interface IDeteccionVisitaCorreoService
{
    Task<Result<DeteccionVisitaCorreoDto>> DetectarAsync(
        string cuerpoMensaje,
        IReadOnlyList<CentroCandidatoVisitaDto> centrosDisponibles,
        DateOnly fechaReferencia,
        CancellationToken cancellationToken = default);
}

public record CentroCandidatoVisitaDto(Guid Id, string Nombre);

/// <summary>
/// <paramref name="CentroId"/>, cuando no es null, siempre es uno de los Id
/// de <see cref="CentroCandidatoVisitaDto"/> pasados a la petición — el
/// proveedor concreto descarta cualquier Id que el modelo alucine fuera de
/// esa lista (ver AnthropicDeteccionVisitaCorreoService).
/// </summary>
public record DeteccionVisitaCorreoDto(
    bool EsSolicitudVisita, Guid? CentroId, DateOnly? FechaInicio, DateOnly? FechaFin, string? Resumen);
