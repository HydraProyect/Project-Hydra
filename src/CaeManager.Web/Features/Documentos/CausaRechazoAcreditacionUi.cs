using CaeManager.Domain.Documentos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>Traduce CausaRechazoAcreditacion a texto — mismo espíritu que EstadoDocumentoUi/TipoItemBandejaUi, un solo sitio de traducción.</summary>
public static class CausaRechazoAcreditacionUi
{
    public static string Texto(CausaRechazoAcreditacion causa) => causa switch
    {
        CausaRechazoAcreditacion.Ilegible => "Documento ilegible",
        CausaRechazoAcreditacion.DocumentoEquivocado => "Documento equivocado",
        CausaRechazoAcreditacion.DatosErroneos => "Datos erróneos",
        CausaRechazoAcreditacion.CaducadoAlPresentar => "Fuera de vigencia",
        CausaRechazoAcreditacion.FaltaFirmaOSello => "Firma o sello ausente",
        CausaRechazoAcreditacion.FormatoNoAdmitido => "Formato no admitido",
        CausaRechazoAcreditacion.Otro => "Otro",
        _ => "—"
    };
}
