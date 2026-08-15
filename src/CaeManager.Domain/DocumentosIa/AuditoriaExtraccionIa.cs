using CaeManager.Domain.Common;

namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Registro de auditoría de cada procesamiento por <c>DocumentAIRouterService</c>
/// (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 3): proveedor usado, tiempo,
/// coste estimado, páginas, confianza e incidencias — como mínimo lo que
/// pedía el Issue #19. Se escribe siempre, incluso cuando el procesamiento
/// falla (para poder ver fallos recurrentes de un proveedor) o cuando el
/// resultado viene de <see cref="ExtraccionIaCache"/> (proveedor "cache",
/// coste 0 — no se volvió a pagar nada).
///
/// <see cref="DocumentoId"/> y la decisión humana posterior (MACRO_PLAN § 6.6,
/// "¿qué hizo la IA y quién lo confirmó?") solo se conocen cuando esta
/// extracción viene de <c>VerificacionIaDocumentoService</c> — un Documento
/// ya existente. Las lecturas de mero triage previas a la creación del
/// Documento (detección de campos al subir, adjunto de correo/WhatsApp) no
/// tienen todavía un Documento al que enlazar: quedan sin decisión, y eso es
/// correcto, no un dato que falte.
/// </summary>
public class AuditoriaExtraccionIa : EntidadConTenant
{
    public const int LongitudHash = 64;
    public const int LongitudMaximaTipoEsperado = 150;
    public const int LongitudMaximaProveedorCodigo = 100;
    public const int LongitudMaximaIncidencias = 1000;

    public string HashSha256 { get; private set; } = string.Empty;
    public string TipoEsperado { get; private set; } = string.Empty;
    public string ProveedorCodigo { get; private set; } = string.Empty;
    public long TiempoProcesamientoMs { get; private set; }
    public decimal? CosteEstimadoOcr { get; private set; }
    public decimal? CosteEstimado { get; private set; }
    public int NumeroPaginas { get; private set; }
    public int ConfianzaGeneral { get; private set; }
    public string? Incidencias { get; private set; }
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>Documento al que se aplicó esta extracción, cuando se conoce en el momento de procesar (ver VerificacionIaDocumentoService). Null en lecturas de triage previas a la creación del Documento.</summary>
    public Guid? DocumentoId { get; private set; }

    /// <summary>Qué pasó después — ver <see cref="RegistrarDecisionHumana"/>. Null mientras no haya decisión (o no aplique, por no tener DocumentoId).</summary>
    public DecisionHumanaIa? DecisionHumana { get; private set; }

    /// <summary>Quién tomó la decisión — solo tiene valor cuando <see cref="DecisionHumana"/> es manual (Confirmada/Descartada); una decisión automática no tiene usuario detrás.</summary>
    public Guid? UsuarioDecisionId { get; private set; }

    public DateTime? FechaDecisionUtc { get; private set; }

    private AuditoriaExtraccionIa()
    {
    }

    private AuditoriaExtraccionIa(
        string hashSha256, string tipoEsperado, string proveedorCodigo, long tiempoProcesamientoMs,
        decimal? costeEstimadoOcr, decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias,
        Guid? documentoId)
    {
        if (string.IsNullOrWhiteSpace(hashSha256) || hashSha256.Length != LongitudHash)
            throw new ArgumentException($"El hash SHA256 debe tener exactamente {LongitudHash} caracteres.", nameof(hashSha256));
        if (string.IsNullOrWhiteSpace(proveedorCodigo))
            throw new ArgumentException("Debe indicarse el código del proveedor (o \"cache\"/\"ninguno\").", nameof(proveedorCodigo));

        HashSha256 = hashSha256;
        TipoEsperado = tipoEsperado.Length > LongitudMaximaTipoEsperado ? tipoEsperado[..LongitudMaximaTipoEsperado] : tipoEsperado;
        ProveedorCodigo = proveedorCodigo.Length > LongitudMaximaProveedorCodigo ? proveedorCodigo[..LongitudMaximaProveedorCodigo] : proveedorCodigo;
        TiempoProcesamientoMs = tiempoProcesamientoMs;
        CosteEstimadoOcr = costeEstimadoOcr;
        CosteEstimado = costeEstimado;
        NumeroPaginas = numeroPaginas;
        ConfianzaGeneral = confianzaGeneral;
        Incidencias = incidencias?.Length > LongitudMaximaIncidencias ? incidencias[..LongitudMaximaIncidencias] : incidencias;
        DocumentoId = documentoId;
    }

    public static AuditoriaExtraccionIa Crear(
        string hashSha256, string tipoEsperado, string proveedorCodigo, long tiempoProcesamientoMs,
        decimal? costeEstimadoOcr, decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias,
        Guid? documentoId = null) =>
        new(hashSha256, tipoEsperado, proveedorCodigo, tiempoProcesamientoMs, costeEstimadoOcr, costeEstimado, numeroPaginas, confianzaGeneral, incidencias, documentoId);

    /// <summary>
    /// Cierra el ciclo "la IA propone, el humano dispone" (§ 0 principio rector
    /// del MACRO_PLAN): registra qué pasó después de esta extracción. Idempotente
    /// por diseño en el sentido contrario — nunca se sobrescribe una decisión ya
    /// tomada, para que el primer cierre (automático o manual) sea el que quede.
    /// </summary>
    public void RegistrarDecisionHumana(DecisionHumanaIa decision, Guid? usuarioId)
    {
        if (DocumentoId is null)
            throw new InvalidOperationException("Solo se puede registrar una decisión humana sobre una auditoría ligada a un Documento.");
        if (DecisionHumana is not null)
            throw new InvalidOperationException("Esta auditoría ya tiene una decisión humana registrada.");
        if (decision == DecisionHumanaIa.AutomaticaSinRevision && usuarioId is not null)
            throw new ArgumentException("Una decisión automática no debe llevar usuario.", nameof(usuarioId));
        if (decision != DecisionHumanaIa.AutomaticaSinRevision && usuarioId is null)
            throw new ArgumentException("Una decisión manual debe indicar qué usuario la tomó.", nameof(usuarioId));

        DecisionHumana = decision;
        UsuarioDecisionId = usuarioId;
        FechaDecisionUtc = DateTime.UtcNow;
    }
}
