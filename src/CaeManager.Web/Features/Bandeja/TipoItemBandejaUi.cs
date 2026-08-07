using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Bandeja;

/// <summary>
/// Traduce TipoItemBandeja a color/etiqueta/texto de la acción primaria —
/// mismo espíritu que EstadoDocumentoUi, un solo sitio de traducción.
/// </summary>
public static class TipoItemBandejaUi
{
    public static TonoBadge Tono(TipoItemBandeja tipo) => tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => TonoBadge.Peligro,
        TipoItemBandeja.Faltante => TonoBadge.Peligro,
        TipoItemBandeja.Vencido => TonoBadge.Peligro,
        TipoItemBandeja.RequisitoPendiente => TonoBadge.Peligro,
        TipoItemBandeja.VisitaUrgente => TonoBadge.Advertencia,
        TipoItemBandeja.Urgente => TonoBadge.Advertencia,
        TipoItemBandeja.RevisionIa => TonoBadge.Advertencia,
        TipoItemBandeja.DeteccionPendiente => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    public static string Texto(TipoItemBandeja tipo) => tipo switch
    {
        TipoItemBandeja.SugerenciaVisitaUrgente => "Visita sorpresa",
        TipoItemBandeja.Faltante => "Falta",
        TipoItemBandeja.Vencido => "Vencido",
        TipoItemBandeja.RequisitoPendiente => "Bloquea el centro",
        TipoItemBandeja.VisitaUrgente => "Visita próxima",
        TipoItemBandeja.Urgente => "Urgente",
        TipoItemBandeja.RevisionIa => "Revisión IA",
        TipoItemBandeja.DeteccionPendiente => "Detección de personal",
        _ => "—"
    };

    public static string TextoAccion(TipoItemBandeja tipo) => tipo switch
    {
        TipoItemBandeja.Faltante => "Subir documento",
        TipoItemBandeja.RevisionIa => "Revisar",
        TipoItemBandeja.RequisitoPendiente => "Ver requisito",
        TipoItemBandeja.SugerenciaVisitaUrgente => "Confirmar visita",
        TipoItemBandeja.VisitaUrgente => "Ver visita",
        TipoItemBandeja.DeteccionPendiente => "Revisar detección",
        _ => "Gestionar"
    };
}
