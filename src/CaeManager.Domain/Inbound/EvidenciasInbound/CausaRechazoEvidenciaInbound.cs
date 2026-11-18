namespace CaeManager.Domain.Inbound.EvidenciasInbound;

/// <summary>
/// Causa tipificada del rechazo de una evidencia Inbound, propia del módulo Inbound (ADR-013 P-6: Inbound crea sus
/// propios enums; primer paso de B-22). Mientras la evidencia guarde su causa en la columna de
/// <c>Domain.Documentos.CausaRechazoAcreditacion</c> (K-5), los nombres y los ordinales son los mismos, y la conversión
/// vive solo en <see cref="EvidenciaInbound"/>. Un test fija la correspondencia en los dos sentidos: si un enum gana o
/// pierde un valor, da rojo.
/// </summary>
public enum CausaRechazoEvidenciaInbound
{
    Ilegible = 0,
    DocumentoEquivocado = 1,
    DatosErroneos = 2,
    CaducadoAlPresentar = 3,
    FaltaFirmaOSello = 4,
    FormatoNoAdmitido = 5,
    Otro = 6
}
