using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Domain.Common;

namespace CaeManager.Application.AsistenteIa.Decisiones;

/// <summary>
/// Proveedor de IA de texto que toma <b>decisiones cerradas</b> sobre una orden
/// escrita por un Gestor CAE: qué orden del catálogo pide, y cuál de los
/// candidatos que TALVEG ya ha determinado nombra para cada dato. Nunca genera
/// texto ni propone un valor que no estuviera en la lista.
/// <para>
/// Mismo criterio de «sugerencia, nunca automática» que
/// <see cref="Comunicaciones.Deteccion.IDeteccionVisitaCorreoService"/>: esta
/// interfaz solo clasifica y selecciona; quien la llama decide qué hacer con el
/// resultado, y toda orden pasa por la confirmación de una persona
/// (<see cref="OrdenAsistida.RequiereConfirmacion"/>).
/// </para>
/// <para>
/// La traducción a preguntas —escotilla de abstención incluida, y las defensas
/// contra la inyección— vive en <see cref="PlanDecisionCerrada"/>, en esta misma
/// capa, para que no dependa del adaptador que toque.
/// </para>
/// </summary>
public interface IDecisionesCerradasAsistenteService
{
    /// <summary>Qué orden de <see cref="CatalogoOrdenesAsistente.Ordenes"/> pide el texto.</summary>
    Task<Result<ClasificacionOrdenDto>> ClasificarOrdenAsync(
        string textoOrden,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Para cada dato solicitado, cuál de sus candidatos nombra el texto. Los
    /// candidatos los filtra antes quien llama, por lo que la persona puede ver:
    /// este puerto no amplía ni comprueba alcance.
    /// </summary>
    Task<Result<IReadOnlyList<SeleccionCandidatoDto>>> SeleccionarCandidatosAsync(
        string textoOrden,
        IReadOnlyList<SeleccionSolicitadaDto> selecciones,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Un valor posible de un dato. <paramref name="Nombre"/> es lo que ve el modelo:
/// lo construye TALVEG a partir de campos estructurados, nunca a partir del texto
/// de la orden.
/// </summary>
public record CandidatoDecisionDto(Guid Id, string Nombre);

/// <summary>Un dato de la orden y los únicos valores entre los que puede elegirse.</summary>
public record SeleccionSolicitadaDto(CampoDeOrden Campo, IReadOnlyList<CandidatoDecisionDto> Candidatos);

/// <summary>
/// <paramref name="OrdenId"/> es null cuando el modelo se abstiene; cuando no lo
/// es, siempre es el Id de una orden del catálogo. <paramref name="Confianza"/>
/// va de 0 a 100, como en los demás puertos de detección.
/// </summary>
public record ClasificacionOrdenDto(string? OrdenId, int Confianza);

/// <summary>
/// <paramref name="CandidatoId"/> es null cuando el modelo se abstiene; cuando no
/// lo es, siempre es uno de los Id de los candidatos pasados para
/// <paramref name="Campo"/>. <paramref name="Confianza"/> va de 0 a 100.
/// </summary>
public record SeleccionCandidatoDto(string Campo, Guid? CandidatoId, int Confianza);
