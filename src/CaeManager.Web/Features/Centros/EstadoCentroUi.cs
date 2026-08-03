using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Centros;

/// <summary>
/// Traduce EstadoCentro a color/etiqueta. Vive en un solo sitio para que la
/// tabla de Centros y el Workspace nunca puedan mostrar el mismo estado con
/// colores o textos distintos — mismo criterio que EstadoDocumentoUi.
/// </summary>
public static class EstadoCentroUi
{
    public static TonoBadge Tono(EstadoCentro estado) => estado switch
    {
        EstadoCentro.Vigente => TonoBadge.Exito,
        EstadoCentro.Proximo => TonoBadge.Advertencia,
        EstadoCentro.Urgente => TonoBadge.Peligro,
        EstadoCentro.Vencido => TonoBadge.Peligro,
        EstadoCentro.Faltante => TonoBadge.Peligro,
        EstadoCentro.Bloqueado => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    public static string Texto(EstadoCentro estado) => estado switch
    {
        EstadoCentro.Vigente => "Vigente",
        EstadoCentro.Proximo => "Próximo",
        EstadoCentro.Urgente => "Urgente",
        EstadoCentro.Vencido => "Vencido",
        EstadoCentro.Faltante => "Falta documentación",
        EstadoCentro.Bloqueado => "Bloqueado",
        _ => "Vigente"
    };
}
