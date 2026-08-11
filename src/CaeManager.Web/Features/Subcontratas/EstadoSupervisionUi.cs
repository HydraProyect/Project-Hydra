using CaeManager.Domain.Subcontratas;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Subcontratas;

/// <summary>
/// Traduce EstadoSupervision y los enums de ADR-005 a color/etiqueta — mismo
/// criterio que <c>EstadoDocumentoUi</c>: un solo sitio para que ninguna
/// pantalla muestre el mismo estado con colores o textos distintos.
/// </summary>
public static class EstadoSupervisionUi
{
    public static TonoBadge Tono(EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.Vigente => TonoBadge.Exito,
        EstadoSupervision.Proximo => TonoBadge.Advertencia,
        EstadoSupervision.Urgente => TonoBadge.Peligro,
        EstadoSupervision.Vencido => TonoBadge.Peligro,
        EstadoSupervision.NoValido => TonoBadge.Peligro,
        _ => TonoBadge.Neutro
    };

    public static string Texto(EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.Vigente => "Vigente",
        EstadoSupervision.Proximo => "Próximo",
        EstadoSupervision.Urgente => "Urgente",
        EstadoSupervision.Vencido => "Vencido",
        EstadoSupervision.NoValido => "No válido",
        _ => "Sin verificar"
    };

    public static string TextoResultado(ResultadoVerificacionExterna resultado) => resultado switch
    {
        ResultadoVerificacionExterna.Valido => "Válido",
        ResultadoVerificacionExterna.NoValido => "No válido",
        _ => "No encontrado"
    };

    public static string TextoNivel(NivelServicioSubcontrata nivel) => nivel switch
    {
        NivelServicioSubcontrata.Supervisada => "Supervisada",
        _ => "Gestionada"
    };

    public static TonoBadge TonoNivel(NivelServicioSubcontrata nivel) => nivel switch
    {
        NivelServicioSubcontrata.Supervisada => TonoBadge.Advertencia,
        _ => TonoBadge.Exito
    };
}
