using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Hechos verificados de una firma digital concreta del archivo de un
/// Documento — 0..N por Documento (los PDFs de la Administración pueden
/// llevar varias firmas apiladas por incremental updates). Registro
/// inmutable del resultado de la verificación: al renovar el archivo se
/// borran las filas del documento y se recrean con el resultado del archivo
/// vigente (ver ValidacionDocumentoOficialService). La decisión agregada
/// (nivel de confianza, auto-validación) vive en
/// <see cref="VerificacionDocumentoOficial"/>, no aquí.
/// </summary>
public class FirmaDigitalDocumento : EntidadConTenant
{
    public const int LongitudMaximaEmisor = 300;
    public const int LongitudMaximaNumeroSerie = 100;
    public const int LongitudMaximaFirmante = 300;
    public const int LongitudMaximaNif = 20;
    public const int LongitudMaximaMotivo = 500;

    public Guid DocumentoId { get; private set; }
    public int Indice { get; private set; }
    public EstadoFirmaPdf Estado { get; private set; }
    public bool CadenaConfiable { get; private set; }
    public ComprobacionRevocacion Revocacion { get; private set; }
    public string EmisorCertificado { get; private set; } = string.Empty;
    public string NumeroSerieCertificado { get; private set; } = string.Empty;
    public bool EsSelloDeOrgano { get; private set; }

    /// <summary>
    /// Nombre y NIF del firmante según su certificado. Restricción RGPD del
    /// plan (PR-4): hasta la confirmación expresa del usuario, quien persiste
    /// solo los rellena cuando <see cref="EsSelloDeOrgano"/> — dato del
    /// organismo, no de una persona física.
    /// </summary>
    public string? FirmanteNombre { get; private set; }
    public string? FirmanteNif { get; private set; }

    public DateTime? FechaFirmaUtc { get; private set; }
    public bool TieneSelloDeTiempo { get; private set; }
    public bool CubreDocumentoCompleto { get; private set; }
    public string? MotivoInvalidez { get; private set; }
    public DateTime VerificadaEnUtc { get; private set; } = DateTime.UtcNow;

    private FirmaDigitalDocumento()
    {
    }

    public FirmaDigitalDocumento(
        Guid documentoId,
        int indice,
        EstadoFirmaPdf estado,
        bool cadenaConfiable,
        ComprobacionRevocacion revocacion,
        string emisorCertificado,
        string numeroSerieCertificado,
        bool esSelloDeOrgano,
        string? firmanteNombre,
        string? firmanteNif,
        DateTime? fechaFirmaUtc,
        bool tieneSelloDeTiempo,
        bool cubreDocumentoCompleto,
        string? motivoInvalidez)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La firma debe estar ligada a un documento.", nameof(documentoId));
        if (indice < 0)
            throw new ArgumentException("El índice de la firma no puede ser negativo.", nameof(indice));

        DocumentoId = documentoId;
        Indice = indice;
        Estado = estado;
        CadenaConfiable = cadenaConfiable;
        Revocacion = revocacion;
        EmisorCertificado = Truncar(emisorCertificado, LongitudMaximaEmisor) ?? string.Empty;
        NumeroSerieCertificado = Truncar(numeroSerieCertificado, LongitudMaximaNumeroSerie) ?? string.Empty;
        EsSelloDeOrgano = esSelloDeOrgano;
        FirmanteNombre = Truncar(firmanteNombre, LongitudMaximaFirmante);
        FirmanteNif = Truncar(firmanteNif, LongitudMaximaNif);
        FechaFirmaUtc = fechaFirmaUtc;
        TieneSelloDeTiempo = tieneSelloDeTiempo;
        CubreDocumentoCompleto = cubreDocumentoCompleto;
        MotivoInvalidez = Truncar(motivoInvalidez, LongitudMaximaMotivo);
    }

    private static string? Truncar(string? valor, int longitudMaxima) =>
        valor?.Length > longitudMaxima ? valor[..longitudMaxima] : valor;
}
