using CaeManager.Application.Documentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Traduce EstadoDocumento a color/etiqueta. Vive en un solo sitio para que
/// Documentos y Alertas nunca puedan mostrar el mismo estado con colores o
/// textos distintos.
/// </summary>
public static class EstadoDocumentoUi
{
    public static TonoBadge Tono(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vigente => TonoBadge.Exito,
        EstadoDocumento.Proximo => TonoBadge.Advertencia,
        EstadoDocumento.Urgente => TonoBadge.Peligro,
        EstadoDocumento.Vencido => TonoBadge.Peligro,
        EstadoDocumento.Faltante => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    public static string Texto(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Vigente => "Vigente",
        EstadoDocumento.Proximo => "Próximo",
        EstadoDocumento.Urgente => "Urgente",
        EstadoDocumento.Vencido => "Vencido",
        EstadoDocumento.Faltante => "Falta",
        _ => "No aplica"
    };

    /// <summary>
    /// Estado documental derivado de Trabajador/Empresa/Vehículo, donde null
    /// significa "no tiene ningún documento todavía" — ver
    /// <see cref="ICalculoEstadoDocumentalService"/>.
    /// </summary>
    public static TonoBadge TonoDocumental(EstadoDocumento? estado) =>
        estado is null ? TonoBadge.Neutro : Tono(estado.Value);

    public static string TextoDocumental(EstadoDocumento? estado) =>
        estado is null ? "Sin documentos" : Texto(estado.Value);

    /// <summary>
    /// Opciones del filtro de estado documental, de peor a mejor: al filtrar,
    /// lo que el gestor busca es lo que le urge. Mismas opciones en las tres
    /// pantallas — es la misma pregunta sobre tres tablas distintas.
    /// </summary>
    public static IReadOnlyList<OpcionEstado> OpcionesDocumentales { get; } =
    [
        new(nameof(EstadoDocumento.Vencido), "Vencido"),
        new(nameof(EstadoDocumento.Urgente), "Urgente"),
        new(nameof(EstadoDocumento.Proximo), "Próximo"),
        new(nameof(EstadoDocumento.Vigente), "Vigente"),
        new(nameof(EstadoDocumento.NoAplica), "No aplica"),
        new(EstadoDocumentalFiltro.SinDocumentos, "Sin documentos")
    ];
}
