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
}
