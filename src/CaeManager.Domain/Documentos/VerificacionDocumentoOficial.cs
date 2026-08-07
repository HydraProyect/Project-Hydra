using CaeManager.Domain.Common;

namespace CaeManager.Domain.Documentos;

/// <summary>
/// Resultado agregado de la validación automática de un documento oficial
/// (verificación de firma + extracción determinista + cotejo) — 0..1 por
/// Documento: siempre refleja el archivo vigente (al renovar se borra y se
/// recrea, ver ValidacionDocumentoOficialService). Las firmas individuales
/// que lo sustentan están en <see cref="FirmaDigitalDocumento"/>.
///
/// La decisión <see cref="DecisionValidacionOficial.AutoValidado"/> es la
/// excepción aprobada a la invariante "el análisis nunca decide solo"
/// (PLAN-FIRMA-DIGITAL-PDF.md § 6.1): solo para documentos con este nivel
/// de trazabilidad — firma válida de emisor confiable y datos cotejados.
/// El nivel de confianza se persiste como dato consultable del documento a
/// propósito: es el filtro natural de futuros conectores de integración
/// ("subir solo lo FirmaValida o mejor", ARQUITECTURA-INTEGRACIONES.md).
/// </summary>
public class VerificacionDocumentoOficial : EntidadConTenant
{
    public const int LongitudMaximaCodigoVerificacion = 100;
    public const int LongitudMaximaCif = 20;
    public const int LongitudMaximaRazonSocial = 300;
    public const int LongitudMaximaPeriodo = 20;
    public const int LongitudMaximaMotivos = 1000;

    public Guid DocumentoId { get; private set; }
    public PerfilDocumentoOficial Perfil { get; private set; }
    public NivelConfianzaDocumental NivelConfianza { get; private set; }

    /// <summary>
    /// CEA (TGSS) o CSV (AEAT) extraído del texto del documento. Es lo que
    /// deja preparada la verificación oficial de la épica 3: cuando exista
    /// la autorización RED, contrastar lo pendiente es un job sobre esta
    /// columna, no una migración.
    /// </summary>
    public string? CodigoVerificacion { get; private set; }

    public string? CifDetectado { get; private set; }
    public string? RazonSocialDetectada { get; private set; }
    public DateOnly? FechaEmisionDetectada { get; private set; }

    /// <summary>Periodo de liquidación en RNT/RLC/ITA (p. ej. "2026-07").</summary>
    public string? PeriodoDetectado { get; private set; }

    public ResultadoCotejoDocumentoOficial ResultadoCotejo { get; private set; }
    public DecisionValidacionOficial Decision { get; private set; }

    /// <summary>Legible para la UI: por qué no se auto-validó (o vacío si se auto-validó).</summary>
    public string Motivos { get; private set; } = string.Empty;

    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private VerificacionDocumentoOficial()
    {
    }

    public VerificacionDocumentoOficial(
        Guid documentoId,
        PerfilDocumentoOficial perfil,
        NivelConfianzaDocumental nivelConfianza,
        string? codigoVerificacion,
        string? cifDetectado,
        string? razonSocialDetectada,
        DateOnly? fechaEmisionDetectada,
        string? periodoDetectado,
        ResultadoCotejoDocumentoOficial resultadoCotejo,
        DecisionValidacionOficial decision,
        string motivos)
    {
        if (documentoId == Guid.Empty)
            throw new ArgumentException("La verificación debe estar ligada a un documento.", nameof(documentoId));
        if (perfil == PerfilDocumentoOficial.Ninguno)
            throw new ArgumentException("La verificación exige un perfil de documento oficial.", nameof(perfil));
        if (decision != DecisionValidacionOficial.AutoValidado && string.IsNullOrWhiteSpace(motivos))
            throw new ArgumentException("Una decisión distinta de auto-validado debe explicar sus motivos.", nameof(motivos));

        DocumentoId = documentoId;
        Perfil = perfil;
        NivelConfianza = nivelConfianza;
        CodigoVerificacion = Truncar(codigoVerificacion, LongitudMaximaCodigoVerificacion);
        CifDetectado = Truncar(cifDetectado, LongitudMaximaCif);
        RazonSocialDetectada = Truncar(razonSocialDetectada, LongitudMaximaRazonSocial);
        FechaEmisionDetectada = fechaEmisionDetectada;
        PeriodoDetectado = Truncar(periodoDetectado, LongitudMaximaPeriodo);
        ResultadoCotejo = resultadoCotejo;
        Decision = decision;
        Motivos = Truncar(motivos, LongitudMaximaMotivos) ?? string.Empty;
    }

    private static string? Truncar(string? valor, int longitudMaxima) =>
        valor?.Length > longitudMaxima ? valor[..longitudMaxima] : valor;
}

/// <summary>Cómo salió el cotejo de los datos extraídos contra los del Documento y su propietario.</summary>
public enum ResultadoCotejoDocumentoOficial
{
    /// <summary>No se llegó a cotejar (firma insuficiente para continuar).</summary>
    NoAplicable = 0,

    /// <summary>Todos los campos obligatorios extraídos y coincidentes.</summary>
    Coincide = 1,

    /// <summary>Algún campo extraído no coincide con lo registrado (CIF, fecha...).</summary>
    Discrepancia = 2,

    /// <summary>El parser no pudo extraer los campos obligatorios del texto.</summary>
    ParserSinDatos = 3,
}

/// <summary>La decisión final del pipeline sobre el documento.</summary>
public enum DecisionValidacionOficial
{
    /// <summary>Todo cuadra: nadie tiene que mirarlo. La excepción aprobada a "el análisis nunca decide solo".</summary>
    AutoValidado = 0,

    /// <summary>Algo no cuadra: queda constancia con motivos y lo mira un Gestor CAE.</summary>
    RevisionRequerida = 1,

    /// <summary>No hay firma válida suficiente para el circuito oficial.</summary>
    SinFirmaValida = 2,
}
