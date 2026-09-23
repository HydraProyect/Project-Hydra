using CaeManager.Domain.Subcontratas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Subcontratas.Recursos;
using Microsoft.Extensions.Localization;

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

    public static string Texto(IStringLocalizer<TextosSubcontratas> textos, EstadoSupervision estado) => estado switch
    {
        EstadoSupervision.Vigente => textos["EstadoSupervisionVigente"],
        EstadoSupervision.Proximo => textos["EstadoSupervisionProximo"],
        EstadoSupervision.Urgente => textos["EstadoSupervisionUrgente"],
        EstadoSupervision.Vencido => textos["EstadoSupervisionVencido"],
        EstadoSupervision.NoValido => textos["EstadoSupervisionNoValido"],
        _ => textos["EstadoSupervisionSinVerificar"]
    };

    public static string TextoResultado(IStringLocalizer<TextosSubcontratas> textos, ResultadoVerificacionExterna resultado) => resultado switch
    {
        ResultadoVerificacionExterna.Valido => textos["ResultadoVerificacionValido"],
        ResultadoVerificacionExterna.NoValido => textos["ResultadoVerificacionNoValido"],
        _ => textos["ResultadoVerificacionNoEncontrado"]
    };

    public static string TextoNivel(IStringLocalizer<TextosSubcontratas> textos, NivelServicioSubcontrata nivel) => nivel switch
    {
        NivelServicioSubcontrata.Supervisada => textos["NivelServicioSupervisada"],
        _ => textos["NivelServicioGestionada"]
    };

    public static TonoBadge TonoNivel(NivelServicioSubcontrata nivel) => nivel switch
    {
        NivelServicioSubcontrata.Supervisada => TonoBadge.Advertencia,
        _ => TonoBadge.Exito
    };
}
