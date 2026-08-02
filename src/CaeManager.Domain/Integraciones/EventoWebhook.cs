using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Cola de ingesta de notificaciones de Microsoft Graph — persistida ANTES
/// de procesar (ver ARQUITECTURA-INTEGRACIONES.md § 6.4: el endpoint de
/// webhook nunca procesa Graph de forma síncrona dentro del request
/// entrante, solo guarda esto y responde 202). Un <c>IHostedService</c>
/// aparte la consume.
/// </summary>
public class EventoWebhook : EntidadConTenant
{
    public const int LongitudMaximaError = 1000;

    /// <summary>Tras este número de intentos fallidos se da por perdido (Procesado=true igualmente) para no reintentar indefinidamente un payload roto.</summary>
    public const int MaximoIntentos = 5;

    public Guid ConexionIntegracionId { get; private set; }
    public string PayloadCrudo { get; private set; } = string.Empty;
    public bool Procesado { get; private set; }
    public int Intentos { get; private set; }
    public string? ErrorProcesado { get; private set; }
    public DateTime FechaRecepcionUtc { get; private set; }

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
        Procesado = false;
        Intentos = 0;
        FechaRecepcionUtc = DateTime.UtcNow;
    }

    public void MarcarProcesado()
    {
        Procesado = true;
        ErrorProcesado = null;
    }

    public void RegistrarFallo(string mensaje)
    {
        if (string.IsNullOrWhiteSpace(mensaje))
            throw new ArgumentException("El fallo debe describir qué pasó.", nameof(mensaje));

        Intentos++;
        ErrorProcesado = mensaje.Length > LongitudMaximaError ? mensaje[..LongitudMaximaError] : mensaje;

        if (Intentos >= MaximoIntentos)
            Procesado = true;
    }
}
