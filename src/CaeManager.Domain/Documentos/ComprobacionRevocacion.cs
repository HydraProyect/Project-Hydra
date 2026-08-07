namespace CaeManager.Domain.Documentos;

/// <summary>Cómo quedó la comprobación de revocación del certificado firmante.</summary>
public enum ComprobacionRevocacion
{
    /// <summary>No se pudo comprobar (OCSP/CRL inaccesibles o el certificado no publica puntos de revocación). Degradación, no error — ver PLAN-FIRMA-DIGITAL-PDF.md § 4.</summary>
    NoDisponible = 0,

    /// <summary>Comprobada en línea contra el OCSP/CRL del emisor.</summary>
    ComprobadaEnLinea = 1,

    /// <summary>Validada con las respuestas OCSP/CRL embebidas en el propio PDF (LTV). Reservado: la lectura del diccionario DSS queda para la épica 2.</summary>
    EmbebidaLtv = 2,
}
