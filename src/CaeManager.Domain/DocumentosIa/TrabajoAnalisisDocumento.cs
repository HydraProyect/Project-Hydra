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

    /// <summary>
    /// No antes de esta hora vuelve a ser candidato a
    /// <see cref="ITrabajoAnalisisDocumentoRepository.ReclamarSiguientePendienteAsync"/>
    /// tras un fallo transitorio. <c>null</c> mientras nunca falló — un
    /// trabajo recién creado es candidato inmediato. Sin esto, un proveedor
    /// caído (IA, Graph, PostgreSQL) hacía que el mismo sondeo cada 5 s
    /// reintentara el mismo trabajo sin esperar nada entre intentos,
    /// agotando <see cref="MaximoIntentos"/> en segundos en vez de dar tiempo
    /// a que el proveedor se recupere.
    /// </summary>
    public DateTime? SiguienteIntentoEnUtc { get; private set; }

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
        SiguienteIntentoEnUtc = null;
    }

    /// <summary>
    /// Apagado cooperativo de la aplicación a mitad de análisis (no un
    /// fallo): vuelve a <see cref="EstadoTrabajoAnalisisDocumento.Pendiente"/>
    /// de inmediato, sin gastar un intento de <see cref="MaximoIntentos"/> ni
    /// aplicar el backoff de <see cref="RegistrarFallo"/> — ninguno de los
    /// dos tiene sentido aquí porque nada salió mal, el proceso solo se
    /// está deteniendo. Antes de esto, el trabajo se quedaba en "Procesando"
    /// hasta que <see cref="RecuperarSiEstancado"/> lo recuperase pasados
    /// los 15 minutos de <c>UmbralEstancado</c> — un redeploy rutinario
    /// costaba ese hueco muerto en vez de reencolarse al instante.
    /// </summary>
    public void DevolverAPendienteTrasCancelacion()
    {
        if (Estado != EstadoTrabajoAnalisisDocumento.Procesando) return;

        Estado = EstadoTrabajoAnalisisDocumento.Pendiente;
        IniciadoEnUtc = null;
        SiguienteIntentoEnUtc = null;
    }

    public void MarcarCompletado()
    {
        Estado = EstadoTrabajoAnalisisDocumento.Completado;
        CompletadoEnUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Retardo base del backoff exponencial: 15 s, 30 s (con
    /// <see cref="MaximoIntentos"/> = 3, el segundo y último reintento antes
    /// de <see cref="EstadoTrabajoAnalisisDocumento.Fallido"/>). Doblarse en
    /// vez de sumarse le da tiempo a un proveedor caído a recuperarse sin
    /// convertir un fallo de segundos en un fallo de minutos si el problema
    /// era puntual.
    /// </summary>
    private const int BackoffBaseSegundos = 15;

    /// <summary>Techo del backoff — un proveedor que lleva caído minutos no necesita un retardo que siga creciendo sin límite.</summary>
    private const int BackoffMaximoSegundos = 300;

    /// <summary>
    /// Mismo criterio de "mejor esfuerzo" que tenía la cola en memoria: un
    /// fallo no invalida el Documento ya guardado. Lo que cambia es que ahora
    /// reintenta hasta <see cref="MaximoIntentos"/> veces antes de darlo por
    /// definitivamente fallido, en vez de perderse en un log la primera vez
    /// — y cada reintento espera un backoff exponencial con jitter en vez de
    /// volver a intentarse en el siguiente sondeo (5 s después): sin esto, un
    /// proveedor de IA caído agotaba los tres intentos en menos de 15 s,
    /// convirtiendo un fallo transitorio en uno definitivo antes de que el
    /// proveedor tuviera ninguna oportunidad de recuperarse.
    /// </summary>
    public void RegistrarFallo(string error)
    {
        RegistrarError(error);

        if (Intentos >= MaximoIntentos)
        {
            Estado = EstadoTrabajoAnalisisDocumento.Fallido;
            SiguienteIntentoEnUtc = null;
            return;
        }

        Estado = EstadoTrabajoAnalisisDocumento.Pendiente;

        var backoffSegundos = Math.Min(BackoffMaximoSegundos, BackoffBaseSegundos * Math.Pow(2, Intentos - 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 5_000));
        SiguienteIntentoEnUtc = DateTime.UtcNow + TimeSpan.FromSeconds(backoffSegundos) + jitter;
    }

    /// <summary>
    /// Fallo que no tiene sentido reintentar (D3: p. ej. el archivo del
    /// Documento ya no existe, <c>FileNotFoundException</c> desde
    /// <c>IFileStorageService.AbrirAsync</c>) — va directo a
    /// <see cref="EstadoTrabajoAnalisisDocumento.Fallido"/> sin gastar los
    /// intentos que le quedaran hasta <see cref="MaximoIntentos"/> en algo
    /// que no puede cambiar de resultado. A diferencia de
    /// <see cref="RegistrarFallo"/>, nunca vuelve a Pendiente: cada intento
    /// adicional sería otra llamada de pago a un proveedor de IA (o, si el
    /// tipo de análisis no llama a IA, otro evento de Sentry) por algo que
    /// ya se sabe que va a fallar igual.
    /// </summary>
    public void RegistrarFalloDefinitivo(string error)
    {
        RegistrarError(error);
        Estado = EstadoTrabajoAnalisisDocumento.Fallido;
    }

    private void RegistrarError(string error)
    {
        Intentos++;
        UltimoError = string.IsNullOrEmpty(error)
            ? null
            : error.Length > LongitudMaximaError ? error[..LongitudMaximaError] : error;
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
