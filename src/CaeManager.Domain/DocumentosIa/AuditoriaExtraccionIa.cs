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
///
/// Reproducibilidad de una extracción: hasta aquí, esta auditoría decía QUÉ
/// proveedor ganó y con qué confianza, pero no con qué versión exacta ni bajo
/// qué prompt/esquema — sin eso, "por qué esta extracción dio este
/// resultado" no se puede reconstruir seis meses después, cuando el
/// proveedor ya sirve otra versión bajo el mismo alias. Tres campos lo
/// cierran: <see cref="ModeloExacto"/> y <see cref="RequestId"/> vienen de la
/// RESPUESTA del proveedor que ganó la estructuración, nunca del alias
/// pedido en la configuración (que puede ser móvil — ver
/// MistralOcrOptions.ModeloChat, por defecto "mistral-small-latest"); ambos
/// quedan null si nunca se llegó a llamar a ningún proveedor (caché, fallo
/// de clasificación/texto, cero proveedores configurados).
/// <see cref="VersionPipeline"/> es la versión vigente de
/// ExtraccionIaCache.VersionPipelineActual al procesar, que ya ata prompt +
/// esquema + reglas de post-proceso (ver su propio comentario) — se
/// registra siempre, incluso en caché o en fallo, así que no hace falta un
/// campo de "versión de prompt" ni de "versión de esquema" por separado:
/// sería la misma cadena duplicada. <see cref="ProveedoresInvocados"/> son
/// todos los códigos que se intentaron para la estructuración de esta
/// llamada (ganaran o no), en el orden en que se probaron.
///
/// Deliberadamente sin región/endpoint de procesamiento: ninguno de los tres
/// proveedores integrados (Anthropic, Gemini, Mistral) ofrece hoy selección
/// de región — es una pregunta que solo tiene sentido una vez exista una
/// política de tratamiento IA por tenant (hallazgo #1 de la auditoría del
/// Módulo 3, decisión pendiente del propietario). Una columna que siempre
/// quedaría a null no sería reproducibilidad, sería aparentarla.
/// </summary>
public class AuditoriaExtraccionIa : EntidadConTenant
{
    public const int LongitudHash = 64;
    public const int LongitudMaximaTipoEsperado = 150;
    public const int LongitudMaximaProveedorCodigo = 100;
    public const int LongitudMaximaIncidencias = 1000;
    public const int LongitudMaximaModeloExacto = 150;
    public const int LongitudMaximaRequestId = 200;
    public const int LongitudMaximaProveedoresInvocados = 300;

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

    /// <summary>Versión de ExtraccionIaCache.VersionPipelineActual vigente al procesar — ata prompt, esquema y reglas de post-proceso a la vez (ver el comentario de esa constante). Siempre tiene valor, incluso en caché o en fallo.</summary>
    public string VersionPipeline { get; private set; } = string.Empty;

    /// <summary>Modelo exacto que respondió (de la respuesta del proveedor, nunca del alias pedido). Null si no se llegó a llamar a ningún proveedor.</summary>
    public string? ModeloExacto { get; private set; }

    /// <summary>Identificador de correlación del lado del proveedor (ver CorrelacionRespuestaIa) para la llamada que ganó la estructuración. Null si no se llegó a llamar a ningún proveedor.</summary>
    public string? RequestId { get; private set; }

    /// <summary>Códigos de todos los proveedores intentados para la estructuración de esta llamada (ganaran o no), separados por coma, en el orden en que se probaron. Null si no se intentó ninguno.</summary>
    public string? ProveedoresInvocados { get; private set; }

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
        Guid? documentoId, string? modeloExacto, string? requestId, string? proveedoresInvocados)
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
        // Constante, no parámetro con valor por defecto elegido por el
        // llamador: así es imposible que una auditoría se escriba con una
        // versión de pipeline distinta de la que de verdad estaba vigente.
        VersionPipeline = ExtraccionIaCache.VersionPipelineActual;
        ModeloExacto = modeloExacto?.Length > LongitudMaximaModeloExacto ? modeloExacto[..LongitudMaximaModeloExacto] : modeloExacto;
        RequestId = requestId?.Length > LongitudMaximaRequestId ? requestId[..LongitudMaximaRequestId] : requestId;
        ProveedoresInvocados = proveedoresInvocados?.Length > LongitudMaximaProveedoresInvocados
            ? proveedoresInvocados[..LongitudMaximaProveedoresInvocados] : proveedoresInvocados;
    }

    public static AuditoriaExtraccionIa Crear(
        string hashSha256, string tipoEsperado, string proveedorCodigo, long tiempoProcesamientoMs,
        decimal? costeEstimadoOcr, decimal? costeEstimado, int numeroPaginas, int confianzaGeneral, string? incidencias,
        Guid? documentoId = null, string? modeloExacto = null, string? requestId = null, string? proveedoresInvocados = null) =>
        new(hashSha256, tipoEsperado, proveedorCodigo, tiempoProcesamientoMs, costeEstimadoOcr, costeEstimado, numeroPaginas, confianzaGeneral, incidencias,
            documentoId, modeloExacto, requestId, proveedoresInvocados);

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
