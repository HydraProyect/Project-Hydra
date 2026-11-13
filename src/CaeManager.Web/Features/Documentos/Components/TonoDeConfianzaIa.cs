using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Documentos.Components;

/// <summary>
/// El corte de color de la confianza de una Revisión IA, uno solo para la pestaña Revisión IA y para la columna «Revisión IA» del
/// listado de Documentos: 95 o más, verde; de 70 a 94, ámbar; menos de 70, rojo.
/// </summary>
public static class TonoDeConfianzaIa
{
    public static TonoBadge De(int confianza) => confianza switch
    {
        >= 95 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };

    /// <summary>El tono como sufijo de clase CSS en minúsculas («exito», «advertencia», «peligro»).</summary>
    public static string Clave(int confianza) => De(confianza).ToString().ToLowerInvariant();
}
