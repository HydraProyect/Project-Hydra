using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Visitas;

/// <summary>Traduce NivelUrgenciaVisita a color/etiqueta — mismo espíritu que EstadoDocumentoUi.</summary>
public static class NivelUrgenciaVisitaUi
{
    public static TonoBadge Tono(NivelUrgenciaVisita nivel) => nivel switch
    {
        NivelUrgenciaVisita.Critica => TonoBadge.Peligro,
        NivelUrgenciaVisita.Urgente => TonoBadge.Advertencia,
        NivelUrgenciaVisita.EnCurso => TonoBadge.Info,
        _ => TonoBadge.Neutro
    };

    public static string Texto(NivelUrgenciaVisita nivel) => nivel switch
    {
        NivelUrgenciaVisita.Critica => "Crítica",
        NivelUrgenciaVisita.Urgente => "Urgente",
        NivelUrgenciaVisita.EnCurso => "En curso",
        _ => "Normal"
    };
}
