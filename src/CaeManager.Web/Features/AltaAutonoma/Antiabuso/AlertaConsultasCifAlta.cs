using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

namespace CaeManager.Web.Features.AltaAutonoma.Antiabuso;

/// <summary>
/// Alerta, sin bloqueo, de consultas repetidas de un mismo CIF o NIF en el paso «Empresa» del alta autónoma (A4): cuando
/// un mismo CIF recibe más de <see cref="Umbral"/> consultas con resultado en 24 horas, se escribe un evento de registro
/// con <see cref="EventoConsultasRepetidas"/> para que Soporte TALVEG vea el barrido. Nunca cambia la respuesta.
///
/// <para>
/// Sustituye al cupo de 3 consultas al día por CIF del § 4.1 del plan (revisión de A4): un contador por CIF compartido
/// entre Solicitudes de alta distintas que bloquea filtra actividad ajena, porque quien se registra con un credencial
/// propio recibiría una respuesta distinta si otras Solicitudes agotaron el cupo de ese CIF. El freno real está en la
/// máquina de estados: cada Solicitud de alta consulta su CIF con resultado una sola vez (de <c>CorreoVerificado</c> solo
/// se sale una vez), y cada Solicitud exige un correo verificado; a eso se suma el límite por IP del paso
/// (<see cref="LimitadorAlta.ParticionOrganizacion"/>).
/// </para>
///
/// <para>
/// La clave de cada contador es un HMAC-SHA256 del CIF normalizado con una clave aleatoria de esta instancia, nunca el CIF
/// en claro: el NIF de una persona física es un dato personal (DA-11). El evento no lleva ni el CIF ni la clave: solo
/// dice que se pasó el umbral. Los contadores viven en memoria por instancia y se reinician con cada despliegue; la
/// ventana es deslizante, en tramos de una hora.
/// </para>
/// </summary>
public sealed class AlertaConsultasCifAlta : IDisposable
{
    /// <summary>Consultas con resultado de un mismo CIF en 24 horas a partir de las cuales cada una más avisa.</summary>
    public const int Umbral = 3;

    /// <summary>Evento estable de la alerta: un CIF consultado más veces de las esperables en 24 horas.</summary>
    public static readonly EventId EventoConsultasRepetidas = new(44003, "AltaAutonomaCifConsultasRepetidas");

    private readonly byte[] _clave = RandomNumberGenerator.GetBytes(32);
    private readonly PartitionedRateLimiter<string> _contador;
    private readonly ILogger<AlertaConsultasCifAlta> _logger;

    /// <summary>Ventana de la alerta en producción: 24 horas.</summary>
    public static readonly TimeSpan VentanaPorDefecto = TimeSpan.FromDays(1);

    public AlertaConsultasCifAlta(ILogger<AlertaConsultasCifAlta> logger)
        : this(logger, VentanaPorDefecto)
    {
    }

    /// <summary>Con la ventana a medida: solo para las pruebas.</summary>
    public AlertaConsultasCifAlta(ILogger<AlertaConsultasCifAlta> logger, TimeSpan ventana)
    {
        _logger = logger;
        Ventana = ventana;
        _contador = PartitionedRateLimiter.Create<string, string>(clave =>
            RateLimitPartition.GetSlidingWindowLimiter(clave, _ => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = Umbral,
                Window = ventana,
                SegmentsPerWindow = 24,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
    }

    /// <summary>Ventana en la que se cuentan las consultas de un mismo CIF.</summary>
    public TimeSpan Ventana { get; }

    /// <summary>
    /// Cuenta una consulta con resultado del CIF ya normalizado y avisa si pasa del umbral. No devuelve nada: quien la
    /// llama no puede hacer depender la respuesta de lo que hayan hecho otras Solicitudes de alta.
    /// </summary>
    public void Registrar(string cifNormalizado)
    {
        ArgumentException.ThrowIfNullOrEmpty(cifNormalizado);
        using var concesion = _contador.AttemptAcquire(Clave(cifNormalizado));
        if (!concesion.IsAcquired)
            _logger.LogWarning(EventoConsultasRepetidas,
                "Un mismo CIF o NIF del alta autónoma lleva más de {Umbral} consultas en 24 horas: posible barrido de CIF. No se bloquea.",
                Umbral);
    }

    private string Clave(string cifNormalizado) =>
        Convert.ToHexString(HMACSHA256.HashData(_clave, Encoding.UTF8.GetBytes(cifNormalizado)));

    public void Dispose() => _contador.Dispose();
}
