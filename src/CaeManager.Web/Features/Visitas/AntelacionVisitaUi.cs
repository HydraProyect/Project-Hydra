using CaeManager.Domain.Visitas;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Visitas;

/// <summary>
/// Traduce la matriz de antelación a etiquetas y tonos — mismo espíritu que
/// <see cref="NivelUrgenciaVisitaUi"/>. Los textos de atribución están escritos
/// desde el punto de vista de quien defiende la gestión: dicen qué pasó, no
/// acusan a nadie.
/// </summary>
public static class AntelacionVisitaUi
{
    public static TonoBadge Tono(TramoAntelacion tramo) => tramo switch
    {
        TramoAntelacion.Expres => TonoBadge.Peligro,
        TramoAntelacion.Urgente => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro
    };

    public static string Texto(TramoAntelacion tramo) => tramo switch
    {
        TramoAntelacion.Expres => "Exprés",
        TramoAntelacion.Urgente => "Urgente",
        _ => "Estándar"
    };

    public static string TextoAtribucion(AtribucionUrgencia atribucion) => atribucion switch
    {
        AtribucionUrgencia.SolicitudTardiaCliente => "El aviso llegó dentro de la ventana de urgencia",
        AtribucionUrgencia.DocumentacionTardiaCliente => "Se avisó con tiempo, pero la documentación llegó al filo",
        AtribucionUrgencia.RetrasoGestor => "Retraso en la gestión",
        _ => "Hubo margen suficiente"
    };

    /// <summary>Horas con un decimal y sin ceros de relleno — "15 h", "71,5 h".</summary>
    public static string Horas(decimal? horas) => horas is null ? "—" : $"{horas.Value:0.#} h";
}
