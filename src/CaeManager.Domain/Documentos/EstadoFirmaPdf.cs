namespace CaeManager.Domain.Documentos;

/// <summary>Resultado criptográfico de una firma concreta de un PDF (un PDF puede llevar varias, apiladas por incremental updates).</summary>
public enum EstadoFirmaPdf
{
    /// <summary>El hash del rango firmado coincide y la firma verifica con el certificado embebido. No dice nada de la confianza en el emisor — eso es <see cref="NivelConfianzaDocumental"/>.</summary>
    Valida = 0,

    /// <summary>La verificación criptográfica falla: el contenido firmado fue alterado, o la firma es incorrecta.</summary>
    Invalida = 1,

    /// <summary>Formato de firma que el verificador no sabe procesar (SubFilter distinto de adbe.pkcs7.detached / ETSI.CAdES.detached). No se puede afirmar ni integridad ni manipulación.</summary>
    NoSoportada = 2,
}
