namespace CaeManager.Domain.Documentos;

/// <summary>
/// Cuánto puede fiarse la plataforma (un Gestor CAE o una regla automática)
/// de lo que dice un Documento, según su firma digital — niveles discretos
/// con nombre, no un score inventado (PLAN-FIRMA-DIGITAL-PDF.md § 1). El
/// orden importa: valores mayores = más confianza; la auto-validación de
/// documentos oficiales exige como mínimo <see cref="FirmaValidaSinRevocacion"/>
/// (decisión § 6.1 del plan).
/// </summary>
public enum NivelConfianzaDocumental
{
    /// <summary>Hay firma pero no cuadra: el documento fue alterado tras firmarse. El único nivel en que la plataforma acusa.</summary>
    Manipulado = 0,

    /// <summary>Sin firma digital: los datos valen lo que valga su extracción (IA/OCR). Estado normal de la mayoría de documentos del CAE.</summary>
    SoloLectura = 1,

    /// <summary>Firma íntegra, pero la cadena del certificado no llega a ninguna CA del almacén de confianza. Informativo, no una acusación.</summary>
    FirmaIntegraEmisorNoConfiable = 2,

    /// <summary>Firma íntegra y de emisor confiable, pero la revocación del certificado no se pudo comprobar (OCSP/CRL inaccesibles).</summary>
    FirmaValidaSinRevocacion = 3,

    /// <summary>Firma íntegra, emisor confiable y revocación comprobada.</summary>
    FirmaValida = 4,

    /// <summary>Además de la firma, el organismo emisor confirmó la emisión (CEA/CSV contrastado). Reservado a la verificación oficial (épica 3) — la verificación de firma nunca lo produce por sí sola.</summary>
    VerificadoOficialmente = 5,
}
