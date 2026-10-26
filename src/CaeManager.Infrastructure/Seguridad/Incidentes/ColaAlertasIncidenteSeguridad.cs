using System.Threading.Channels;
using CaeManager.Application.Common;
using CaeManager.Application.Seguridad.Incidentes;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Seguridad.Incidentes;

/// <summary>
/// Cola en memoria de alertas de incidente (mismo patrón que <c>RecuperacionContrasena</c>):
/// <see cref="Encolar"/> no bloquea a la operación que disparó la señal, y un único lector
/// envía por el canal con reintentos espaciados.
///
/// <para>
/// <b>Si el canal falla o no existe</b>, el incidente ya está en la base (se registra antes
/// de encolar) y la alerta cae al segundo camino: <see cref="IAlertaOperativa"/> en nivel
/// crítico (Sentry, que también llega a la guardia) y el log como error. El mensaje solo
/// lleva el identificador del incidente. Un proceso que muere con alertas en la cola las
/// pierde, no los incidentes, que quedan en la base sin alerta (deuda anotada para el incremento 3:
/// marca de alerta enviada y barrido al arrancar). El lector es <see cref="EnvioAlertasIncidenteSeguridadHostedService"/>.
/// </para>
/// </summary>
public sealed class ColaAlertasIncidenteSeguridad : IAlertaIncidenteSeguridad
{
    /// <summary>Esperas entre intentos: cuatro intentos en algo más de seis minutos.</summary>
    public static readonly TimeSpan[] EsperasEntreIntentos = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)];

    private const int Capacidad = 256;

    private readonly Channel<Guid> _cola = Channel.CreateBounded<Guid>(new BoundedChannelOptions(Capacidad)
    {
        FullMode = BoundedChannelFullMode.Wait, // con Wait, TryWrite devuelve false si está llena y se avisa
        SingleReader = true,
    });

    private readonly ICanalAlertaIncidenteSeguridad _canal;
    private readonly IAlertaOperativa _alertaOperativa;
    private readonly ILogger<ColaAlertasIncidenteSeguridad> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;

    public ColaAlertasIncidenteSeguridad(
        ICanalAlertaIncidenteSeguridad canal, IAlertaOperativa alertaOperativa, ILogger<ColaAlertasIncidenteSeguridad> logger)
        : this(canal, alertaOperativa, logger, Task.Delay)
    {
    }

    /// <summary>Para los tests: la espera entre intentos, sustituible.</summary>
    public ColaAlertasIncidenteSeguridad(
        ICanalAlertaIncidenteSeguridad canal, IAlertaOperativa alertaOperativa, ILogger<ColaAlertasIncidenteSeguridad> logger,
        Func<TimeSpan, CancellationToken, Task> esperar)
    {
        _canal = canal;
        _alertaOperativa = alertaOperativa;
        _logger = logger;
        _esperar = esperar;
    }

    public void Encolar(Guid incidenteId)
    {
        try
        {
            if (!_canal.Activo)
            {
                _logger.LogError(
                    "Incidente de seguridad sospechado {IncidenteId} sin alerta saliente: el canal está desactivado (IncidentesSeguridad:UrlAlertaSaliente vacía).",
                    incidenteId);
                _alertaOperativa.Emitir(MensajeOperativo(incidenteId, "canal de alerta desactivado"), NivelAlertaOperativa.Critica);
                return;
            }

            if (!_cola.Writer.TryWrite(incidenteId))
            {
                _logger.LogError("Incidente de seguridad sospechado {IncidenteId}: la cola de alertas está llena.", incidenteId);
                _alertaOperativa.Emitir(MensajeOperativo(incidenteId, "cola de alertas llena"), NivelAlertaOperativa.Critica);
            }
        }
        catch (Exception ex)
        {
            // Encolar no lanza nunca hacia la operación que disparó la señal.
            _logger.LogError(ex, "No se pudo encolar la alerta del incidente de seguridad {IncidenteId}.", incidenteId);
        }
    }

    /// <summary>Lado lector de la cola, para el servicio de fondo que envía.</summary>
    public ChannelReader<Guid> Lector => _cola.Reader;

    /// <summary>Envía con reintentos; devuelve si salió. Nunca lanza salvo cancelación de la parada.</summary>
    public async Task<bool> EnviarConReintentosAsync(Guid incidenteId, CancellationToken ct)
    {
        for (var intento = 0; ; intento++)
        {
            try
            {
                await _canal.EnviarAsync(incidenteId, ct);
                _logger.LogInformation("Alerta del incidente de seguridad {IncidenteId} enviada (intento {Intento}).", incidenteId, intento + 1);
                return true;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                if (intento >= EsperasEntreIntentos.Length)
                {
                    _logger.LogError(ex, "La alerta del incidente de seguridad {IncidenteId} no salió tras {Intentos} intentos.",
                        incidenteId, intento + 1);
                    _alertaOperativa.Emitir(MensajeOperativo(incidenteId, "el canal de alerta falló"), NivelAlertaOperativa.Critica);
                    return false;
                }

                _logger.LogWarning(ex, "Falló el intento {Intento} de alerta del incidente de seguridad {IncidenteId}; se reintenta.",
                    intento + 1, incidenteId);
                await _esperar(EsperasEntreIntentos[intento], ct);
            }
        }
    }

    private static string MensajeOperativo(Guid incidenteId, string causa)
        => $"Incidente de seguridad sospechado {incidenteId:D} ({causa}). Revisar en /plataforma/incidentes/{incidenteId:D}.";
}
