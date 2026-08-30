using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Cola de ingesta de notificaciones de Microsoft Graph y WhatsApp Cloud API
/// — persistida ANTES de procesar (ver ARQUITECTURA-INTEGRACIONES.md § 6.4:
/// el endpoint de webhook nunca procesa de forma síncrona dentro del request
/// entrante, solo guarda esto y responde 202). Un <c>IHostedService</c>
/// aparte la consume por proveedor.
/// </summary>
public class EventoWebhook : EntidadConTenant
{
    public const int LongitudMaximaError = 1000;

    /// <summary>Tras este número de intentos fallidos se da por perdido (<see cref="EstadoEventoWebhook.DescartadoDefinitivo"/>) para no reintentar indefinidamente un payload roto.</summary>
    public const int MaximoIntentos = 5;

    private const int BackoffBaseSegundos = 10;
    private const int BackoffMaximoSegundos = 300;

    /// <summary>Marcador que reemplaza el contenido tras <see cref="RedactarPayload"/> — nunca vacío, para no romper la restricción NOT NULL ni el invariante "el evento no puede tener un payload vacío" del constructor.</summary>
    public const string MarcadorPayloadRedactado = "[redactado por retención]";

    public Guid ConexionIntegracionId { get; private set; }
    public string PayloadCrudo { get; private set; } = string.Empty;
    public EstadoEventoWebhook Estado { get; private set; } = EstadoEventoWebhook.Pendiente;
    public int Intentos { get; private set; }
    public string? ErrorProcesado { get; private set; }
    public DateTime FechaRecepcionUtc { get; private set; }
    public DateTime? IniciadoEnUtc { get; private set; }

    /// <summary>
    /// True una vez que <see cref="RedactarPayload"/> ha reemplazado el
    /// contenido real (auditoría módulo 6): el payload crudo de WhatsApp/
    /// Graph contiene PHI/PII (cuerpos, teléfonos, nombres) que no hace
    /// falta conservar una vez el evento terminó — ni para reintentar (ya no
    /// se va a reintentar, ver <see cref="Estado"/>) ni para depurar pasada
    /// la ventana de retención. Bandera explícita en vez de comparar contra
    /// <see cref="MarcadorPayloadRedactado"/>: barata de indexar y no
    /// depende de que el marcador nunca coincida por casualidad con un
    /// payload real.
    /// </summary>
    public bool PayloadRedactado { get; private set; }

    /// <summary>No antes de esta hora vuelve a ser candidato a reclamo tras un fallo transitorio. Ver <see cref="TrabajoAnalisisDocumento"/> para el mismo razonamiento.</summary>
    public DateTime? SiguienteIntentoEnUtc { get; private set; }

    private EventoWebhook()
    {
    }

    public EventoWebhook(Guid conexionIntegracionId, string payloadCrudo)
    {
        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("El evento debe pertenecer a una conexión.", nameof(conexionIntegracionId));
        if (string.IsNullOrWhiteSpace(payloadCrudo))
            throw new ArgumentException("El evento no puede tener un payload vacío.", nameof(payloadCrudo));

        ConexionIntegracionId = conexionIntegracionId;
        PayloadCrudo = payloadCrudo;
        Estado = EstadoEventoWebhook.Pendiente;
        Intentos = 0;
        FechaRecepcionUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Reclamo por parte del consumidor — ver
    /// <c>IEventoWebhookRepository.ReclamarSiguientePendienteAsync</c>, que
    /// llama a esto dentro de la misma transacción corta que el <c>FOR
    /// UPDATE SKIP LOCKED</c> que lo selecciona.
    /// </summary>
    public void MarcarEnProceso()
    {
        Estado = EstadoEventoWebhook.Procesando;
        IniciadoEnUtc = DateTime.UtcNow;
        SiguienteIntentoEnUtc = null;
    }

    /// <summary>Apagado cooperativo a mitad de ingesta — ver <see cref="TrabajoAnalisisDocumento.DevolverAPendienteTrasCancelacion"/> para el mismo razonamiento.</summary>
    public void DevolverAPendienteTrasCancelacion()
    {
        if (Estado != EstadoEventoWebhook.Procesando) return;

        Estado = EstadoEventoWebhook.Pendiente;
        IniciadoEnUtc = null;
        SiguienteIntentoEnUtc = null;
    }

    public void MarcarProcesado()
    {
        Estado = EstadoEventoWebhook.Completado;
        ErrorProcesado = null;
        IniciadoEnUtc = null;
        SiguienteIntentoEnUtc = null;
    }

    /// <summary>
    /// Backoff exponencial con jitter antes de volver a
    /// <see cref="EstadoEventoWebhook.Pendiente"/> — ver
    /// <see cref="TrabajoAnalisisDocumento.RegistrarFallo"/> para el mismo
    /// razonamiento (sin esto, un proveedor caído agotaba
    /// <see cref="MaximoIntentos"/> en el siguiente sondeo, segundos después,
    /// en vez de darle tiempo a recuperarse).
    /// </summary>
    public void RegistrarFallo(string mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje))
            throw new ArgumentException("El fallo debe describir qué pasó.", nameof(mensaje));

        Intentos++;
        ErrorProcesado = mensaje.Length > LongitudMaximaError ? mensaje[..LongitudMaximaError] : mensaje;
        IniciadoEnUtc = null;

        if (Intentos >= MaximoIntentos)
        {
            Estado = EstadoEventoWebhook.DescartadoDefinitivo;
            SiguienteIntentoEnUtc = null;
            return;
        }

        Estado = EstadoEventoWebhook.Pendiente;

        var backoffSegundos = Math.Min(BackoffMaximoSegundos, BackoffBaseSegundos * Math.Pow(2, Intentos - 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 3_000));
        SiguienteIntentoEnUtc = DateTime.UtcNow + TimeSpan.FromSeconds(backoffSegundos) + jitter;
    }

    /// <summary>
    /// Un evento que quedó "Procesando" cuando el proceso se cayó o se
    /// redesplegó a mitad de ingesta no vuelve solo — ver
    /// <see cref="TrabajoAnalisisDocumento.RecuperarSiEstancado"/> para el
    /// mismo razonamiento.
    /// </summary>
    public void RecuperarSiEstancado(TimeSpan umbral, DateTime ahoraUtc)
    {
        if (Estado != EstadoEventoWebhook.Procesando) return;
        if (IniciadoEnUtc is null || ahoraUtc - IniciadoEnUtc.Value < umbral) return;

        RegistrarFallo("Recuperado tras quedar en \"Procesando\" sin terminar (proceso reiniciado o caído).");
    }

    /// <summary>
    /// Reemplaza <see cref="PayloadCrudo"/> por <see cref="MarcadorPayloadRedactado"/>
    /// — solo permitido sobre un evento en un estado terminal
    /// (<see cref="EstadoEventoWebhook.Completado"/> o
    /// <see cref="EstadoEventoWebhook.DescartadoDefinitivo"/>): uno
    /// <see cref="EstadoEventoWebhook.Pendiente"/> o
    /// <see cref="EstadoEventoWebhook.Procesando"/> todavía puede necesitar
    /// el contenido real para reintentar (auditoría módulo 6, hallazgo de
    /// retención de PayloadCrudo).
    /// </summary>
    public void RedactarPayload()
    {
        if (Estado != EstadoEventoWebhook.Completado && Estado != EstadoEventoWebhook.DescartadoDefinitivo)
        {
            throw new InvalidOperationException(
                "Solo se puede redactar el payload de un evento en un estado terminal (Completado o DescartadoDefinitivo).");
        }

        if (PayloadRedactado) return;

        PayloadCrudo = MarcadorPayloadRedactado;
        PayloadRedactado = true;
    }
}
