using CaeManager.Domain.Common;

namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Cola durable de análisis IA sobre Documentos (P2 #22 de
/// docs/business/MATURITY_REVIEW.md) — sustituye a la cola en memoria sobre
/// <c>Channel&lt;T&gt;</c> que existía antes: un reinicio del proceso perdía
/// cualquier encargo pendiente sin dejar rastro. Al vivir en la misma
/// transacción que crea el Documento (ver <c>CrearDocumentoCommandHandler</c>),
/// el encolado ya no puede perderse entre el guardado del Documento y el
/// encolado en memoria — ambos se confirman juntos o ninguno.
///
/// <see cref="ProcesadorAnalisisDocumentoHostedService"/> (Infrastructure) la
/// consume por sondeo, un tenant a la vez con <c>AmbitoTenantExplicito</c>
/// (docs/MULTITENANCY.md § 8.4) — nunca con una consulta que cruce tenants.
/// </summary>
public class TrabajoAnalisisDocumento : EntidadConTenant
{
    public const int MaximoIntentos = 3;
    public const int LongitudMaximaError = 2000;

    public Guid DocumentoId { get; private set; }
    public Guid? UsuarioSolicitanteId { get; private set; }
    public TipoAnalisisDocumento Tipo { get; private set; }
    public EstadoTrabajoAnalisisDocumento Estado { get; private set; } = EstadoTrabajoAnalisisDocumento.Pendiente;
    public int Intentos { get; private set; }
    public string? UltimoError { get; private set; }
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? IniciadoEnUtc { get; private set; }
    public DateTime? CompletadoEnUtc { get; private set; }

    private TrabajoAnalisisDocumento()
    {
    }

    public TrabajoAnalisisDocumento(Guid documentoId, Guid? usuarioSolicitanteId, TipoAnalisisDocumento tipo)
    {
        DocumentoId = documentoId;
        UsuarioSolicitanteId = usuarioSolicitanteId;
        Tipo = tipo;
    }

    public void MarcarEnProceso()
    {
        Estado = EstadoTrabajoAnalisisDocumento.Procesando;
        IniciadoEnUtc = DateTime.UtcNow;
    }

    public void MarcarCompletado()
    {
        Estado = EstadoTrabajoAnalisisDocumento.Completado;
        CompletadoEnUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Mismo criterio de "mejor esfuerzo" que tenía la cola en memoria: un
    /// fallo no invalida el Documento ya guardado. Lo que cambia es que ahora
    /// reintenta hasta <see cref="MaximoIntentos"/> veces antes de darlo por
    /// definitivamente fallido, en vez de perderse en un log la primera vez.
    /// </summary>
    public void RegistrarFallo(string error)
    {
        Intentos++;
        UltimoError = string.IsNullOrEmpty(error)
            ? null
            : error.Length > LongitudMaximaError ? error[..LongitudMaximaError] : error;
        Estado = Intentos >= MaximoIntentos ? EstadoTrabajoAnalisisDocumento.Fallido : EstadoTrabajoAnalisisDocumento.Pendiente;
        IniciadoEnUtc = null;
    }

    /// <summary>
    /// Un trabajo que quedó "Procesando" cuando el proceso se cayó o se
    /// redesplegó a mitad de análisis no vuelve solo a "Pendiente" — sin
    /// esto, quedaría atascado ahí para siempre y ni se reintentaría ni se
    /// marcaría como fallido. Cuenta como un intento fallido más: tres
    /// caídas seguidas a mitad de este trabajo también lo llevan a
    /// <see cref="EstadoTrabajoAnalisisDocumento.Fallido"/>, no un reintento
    /// infinito.
    /// </summary>
    public void RecuperarSiEstancado(TimeSpan umbral, DateTime ahoraUtc)
    {
        if (Estado != EstadoTrabajoAnalisisDocumento.Procesando) return;
        if (IniciadoEnUtc is null || ahoraUtc - IniciadoEnUtc.Value < umbral) return;

        RegistrarFallo("Recuperado tras quedar en \"Procesando\" sin terminar (proceso reiniciado o caído).");
    }
}
