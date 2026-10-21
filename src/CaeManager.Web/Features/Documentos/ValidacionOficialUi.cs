using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Cómo se nombra y se colorea el nivel de confianza de la validación de documento oficial: una sola definición para el
/// panel del Documento y la página «Documento 360».
/// </summary>
public static class ValidacionOficialUi
{
    public static TonoBadge TonoDeNivel(NivelConfianzaDocumental nivel) => nivel switch
    {
        NivelConfianzaDocumental.VerificadoOficialmente or NivelConfianzaDocumental.FirmaValida => TonoBadge.Exito,
        NivelConfianzaDocumental.FirmaValidaSinRevocacion => TonoBadge.Info,
        NivelConfianzaDocumental.Manipulado => TonoBadge.Peligro,
        NivelConfianzaDocumental.FirmaIntegraEmisorNoConfiable => TonoBadge.Advertencia,
        _ => TonoBadge.Neutro,
    };

    public static string TextoDeNivel(NivelConfianzaDocumental nivel) => nivel switch
    {
        NivelConfianzaDocumental.VerificadoOficialmente => "Verificado oficialmente",
        NivelConfianzaDocumental.FirmaValida => "Firma válida",
        NivelConfianzaDocumental.FirmaValidaSinRevocacion => "Firma válida (revocación no comprobada)",
        NivelConfianzaDocumental.FirmaIntegraEmisorNoConfiable => "Firma íntegra, emisor no reconocido",
        NivelConfianzaDocumental.Manipulado => "Documento manipulado",
        _ => "Sin firma digital",
    };
}
