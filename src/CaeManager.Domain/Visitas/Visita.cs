using CaeManager.Domain.Common;

namespace CaeManager.Domain.Visitas;

/// <summary>
/// Programación de la entrada de uno o varios Trabajadores a un Centro
/// durante un rango de fechas — lo que el cliente comunica como "el
/// trabajador X debe entrar en el Centro Y del día A al día B". No es una
/// Asignacion (que es la relación permanente Trabajador↔Centro): una Visita
/// es puntual y temporal, pensada para verificar de antemano que la
/// documentación de la Empresa y de los Trabajadores implicados está en
/// regla antes de que la visita ocurra. Al pasar FechaFin, la visita deja de
/// aparecer en las vistas activas pero no se borra — igual que Asignacion
/// con FechaBaja, el historial se conserva siempre.
/// </summary>
public class Visita : EntidadBase
{
    public const int LongitudMaximaNotas = 1000;

    public Guid CentroId { get; private set; }
    public DateOnly FechaInicio { get; private set; }
    public DateOnly FechaFin { get; private set; }
    public bool NotificadoCliente { get; private set; }
    public string? Notas { get; private set; }

    /// <summary>Cómo llegó la petición de agendar esta visita (Correo cuando viene de una SugerenciaVisitaCorreo confirmada) — no afecta al flujo de alta, solo de dónde nació la solicitud.</summary>
    public OrigenVisita Origen { get; private set; }

    /// <summary>
    /// Hora de entrada, cuando el cliente la indicó. Deliberadamente opcional y aparte
    /// de <see cref="FechaInicio"/>/<see cref="FechaFin"/>, que siguen siendo
    /// <see cref="DateOnly"/>: la planificación y <see cref="CalculadoraUrgenciaVisita"/>
    /// no cambian, esto solo da resolución horaria a la matriz de antelación. Sin ella se
    /// asume la hora de apertura de la jornada del tenant.
    /// </summary>
    public TimeOnly? HoraEstimadaAcceso { get; private set; }

    /// <summary>Conversación de la que nació la solicitud. Null en visitas creadas a mano desde /visitas, que quedan fuera de las métricas de fricción por no tener nada que medir.</summary>
    public Guid? ConversacionOrigenId { get; private set; }

    /// <summary>Instante del primer mensaje entrante que pidió esta visita — el momento que el cliente considera "yo avisé".</summary>
    public DateTime? FechaHoraSolicitudUtc { get; private set; }

    /// <summary>Instante en que dejó de faltar documentación. Se sella una sola vez y no se reabre: es el punto de corte que decide la atribución.</summary>
    public DateTime? FechaHoraExpedienteCompletoUtc { get; private set; }

    public decimal? AntelacionNominalHoras { get; private set; }
    public decimal? AntelacionEfectivaHoras { get; private set; }
    public TramoAntelacion? Tramo { get; private set; }
    public AtribucionUrgencia Atribucion { get; private set; } = AtribucionUrgencia.SinUrgencia;

    public bool EstaActiva(DateOnly hoy) => FechaFin >= hoy;

    /// <summary>
    /// Momento de entrada normalizado a <see cref="DateTime"/> para poder restarlo de los
    /// instantes de solicitud y de expediente completo. Cae en la hora de apertura de la
    /// jornada cuando el cliente no indicó hora: si alguien tiene visita "el jueves" sin
    /// más, tanto la planta como el portal asumen disponibilidad desde que abre el centro.
    /// </summary>
    public DateTime ObtenerFechaHoraEntrada(TimeOnly horaInicioJornadaDefault)
        => FechaInicio.ToDateTime(HoraEstimadaAcceso ?? horaInicioJornadaDefault, DateTimeKind.Utc);

    private Visita()
    {
    }

    public Visita(Guid centroId, DateOnly fechaInicio, DateOnly fechaFin, string? notas, OrigenVisita origen = OrigenVisita.Plataforma, TimeOnly? horaEstimadaAcceso = null)
    {
        if (centroId == Guid.Empty)
            throw new ArgumentException("La visita debe tener un centro.", nameof(centroId));

        CentroId = centroId;
        EstablecerFechas(fechaInicio, fechaFin);
        EstablecerNotas(notas);
        Origen = origen;
        HoraEstimadaAcceso = horaEstimadaAcceso;
    }

    public void Actualizar(DateOnly fechaInicio, DateOnly fechaFin, string? notas, TimeOnly? horaEstimadaAcceso = null)
    {
        EstablecerFechas(fechaInicio, fechaFin);
        EstablecerNotas(notas);
        HoraEstimadaAcceso = horaEstimadaAcceso;
    }

    public void MarcarNotificadoCliente(bool notificado) => NotificadoCliente = notificado;

    /// <summary>Ata la visita a la conversación que la pidió. Idempotente: una vez atada no se reasigna, para que el instante de solicitud no se mueva bajo los pies del cálculo ya sellado.</summary>
    public void RegistrarOrigenSolicitud(Guid conversacionId, DateTime fechaHoraSolicitudUtc)
    {
        if (ConversacionOrigenId is not null) return;

        ConversacionOrigenId = conversacionId;
        FechaHoraSolicitudUtc = fechaHoraSolicitudUtc;
    }

    /// <summary>
    /// Sella el momento en que dejó de faltar documentación y congela la atribución.
    /// De un solo sentido: si más adelante se añade un trabajador y vuelve a faltar
    /// papel, el sello no se retira — el margen que tuvo el gestor para la solicitud
    /// original no deja de haber existido. Devuelve false si ya estaba sellado o si no
    /// hay solicitud de origen que medir.
    /// </summary>
    public bool MarcarExpedienteCompleto(DateTime fechaHoraUtc, ResultadoAntelacion antelacion)
    {
        if (FechaHoraExpedienteCompletoUtc is not null || FechaHoraSolicitudUtc is null)
            return false;

        FechaHoraExpedienteCompletoUtc = fechaHoraUtc;
        AntelacionNominalHoras = antelacion.NominalHoras;
        AntelacionEfectivaHoras = antelacion.EfectivaHoras;
        Tramo = antelacion.Tramo;
        Atribucion = antelacion.Atribucion;
        return true;
    }

    private void EstablecerFechas(DateOnly fechaInicio, DateOnly fechaFin)
    {
        if (fechaFin < fechaInicio)
            throw new ArgumentException("La fecha de fin no puede ser anterior a la fecha de inicio.", nameof(fechaFin));

        FechaInicio = fechaInicio;
        FechaFin = fechaFin;
    }

    private void EstablecerNotas(string? notas)
    {
        if (notas is not null && notas.Length > LongitudMaximaNotas)
            throw new ArgumentException($"Las notas no pueden superar {LongitudMaximaNotas} caracteres.", nameof(notas));

        Notas = notas;
    }
}
