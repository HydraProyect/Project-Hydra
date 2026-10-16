using CaeManager.Domain.Centros;
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
/// <para>
/// FS-11 (auditoría UX de flujos sin salida, 2026-09-24): cancelar una Visita
/// ya no es un borrado lógico. <see cref="Cancelar"/> la deja en estado
/// Cancelada con un motivo opcional —fuera de Mi trabajo, del Calendario y de
/// los contadores, pero en el historial— y <see cref="Reactivar"/> la devuelve
/// al estado previo. Quién cancela o reactiva no se guarda aquí: lo registra la
/// auditoría, que separa el Actor real del Usuario simulado.
/// </para>
/// </summary>
public class Visita : EntidadBase
{
    public const int LongitudMaximaNotas = 1000;
    public const int LongitudMaximaMotivo = 500;

    /// <summary>
    /// Igual que <see cref="Centro.LongitudMaximaContacto"/>: el nombre se rellena por defecto copiando el contacto
    /// del Centro, que es texto libre de hasta esa longitud, y la copia no puede fallar ni recortarse.
    /// </summary>
    public const int LongitudMaximaContactoNombre = Centro.LongitudMaximaContacto;
    public const int LongitudMaximaContactoTelefono = 50;
    public const int LongitudMaximaContactoCorreo = 254;

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

    /// <summary>
    /// Instante en que dejó de faltar documentación por primera vez. Se sella una sola vez y no
    /// se reabre: es el punto de corte que decide la atribución de la matriz de antelación, y
    /// nada más. No es el estado de la columna «Documentación» ni dice que la Visita esté
    /// gestionada: eso es <see cref="DocumentacionGestionadaEnUtc"/>, que sí se borra.
    /// </summary>
    public DateTime? FechaHoraExpedienteCompletoUtc { get; private set; }

    /// <summary>
    /// Instante en que la documentación de esta Visita se dio por gestionada ante el titular
    /// del Centro: al enviar el paquete de acreditación desde la Visita o al marcarlo a mano
    /// (decisión del propietario, 2026-10-09). Null = «Por gestionar», aunque todos los
    /// documentos estén vigentes: es un estado guardado, no un cálculo sobre los documentos.
    /// Lo borran añadir o quitar un Trabajador (<see cref="RegistrarCambioDeTrabajadores"/>) y
    /// deshacer la marca (<see cref="QuitarMarcaDocumentacionGestionada"/>);
    /// que un documento venza después, no. Independiente de <see cref="NotificadoCliente"/>
    /// («Avisada») y del sello <see cref="FechaHoraExpedienteCompletoUtc"/>.
    /// </summary>
    public DateTime? DocumentacionGestionadaEnUtc { get; private set; }

    public bool DocumentacionGestionada => DocumentacionGestionadaEnUtc is not null;

    public decimal? AntelacionNominalHoras { get; private set; }
    public decimal? AntelacionEfectivaHoras { get; private set; }
    public TramoAntelacion? Tramo { get; private set; }
    public AtribucionUrgencia Atribucion { get; private set; } = AtribucionUrgencia.SinUrgencia;

    /// <summary>FS-11: cancelada de forma reversible. Una Visita cancelada no está activa aunque su FechaFin no haya pasado.</summary>
    public bool EstaCancelada { get; private set; }

    /// <summary>Instante de la última cancelación. Null si no está cancelada.</summary>
    public DateTime? CanceladaEnUtc { get; private set; }

    /// <summary>Motivo opcional de la última cancelación. Null si no está cancelada o no se dio.</summary>
    public string? MotivoCancelacion { get; private set; }

    /// <summary>
    /// Instante de la última reactivación. Null si no se ha reactivado desde la
    /// última cancelación. Existe para que el registro de auditoría de la
    /// reactivación lleve fecha y motivo propios, no solo la cancelación que deshace.
    /// </summary>
    public DateTime? ReactivadaEnUtc { get; private set; }

    /// <summary>Motivo opcional de la última reactivación.</summary>
    public string? MotivoReactivacion { get; private set; }

    /// <summary>
    /// Contacto de la Visita (decisión de producto, 2026-10-16): la persona a la que se dirige quien entra en el
    /// Centro para esta Visita. Es un campo propio de la Visita, no una referencia al Centro: al crearla se rellena
    /// por defecto con el contacto del Centro (<see cref="RellenarContactoDesdeCentro"/>) y después puede cambiarse
    /// por otra persona (<see cref="ActualizarContacto"/>) sin tocar el Centro. Los tres campos son datos personales:
    /// la auditoría los enmascara y la ficha solo los enseña con la política de lectura de las fichas 360.
    /// </summary>
    public string? ContactoNombre { get; private set; }

    /// <inheritdoc cref="ContactoNombre"/>
    public string? ContactoTelefono { get; private set; }

    /// <inheritdoc cref="ContactoNombre"/>
    public string? ContactoCorreo { get; private set; }

    public bool EstaActiva(DateOnly hoy) => !EstaCancelada && FechaFin >= hoy;

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

    /// <summary>
    /// Origen de una Visita que se crea por el comando de alta. Con sugerencia, el del canal del
    /// mensaje del que nació (WhatsApp si fue por WhatsApp; Correo en cualquier otro caso, también
    /// si el mensaje ya no se encuentra); sin sugerencia la ha dado de alta una persona a mano.
    /// </summary>
    /// <param name="desdeSugerencia">El alta viene de una sugerencia detectada en una conversación.</param>
    /// <param name="canalDelMensaje">Canal del mensaje de la sugerencia, si se pudo leer.</param>
    public static OrigenVisita OrigenAlCrear(bool desdeSugerencia, CaeManager.Domain.Comunicaciones.CanalConversacion? canalDelMensaje = null) =>
        !desdeSugerencia
            ? OrigenVisita.Manual
            : canalDelMensaje == CaeManager.Domain.Comunicaciones.CanalConversacion.WhatsApp ? OrigenVisita.WhatsApp : OrigenVisita.Correo;

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

    /// <summary>
    /// Sustituye el contacto de la Visita. Un campo vacío o solo con espacios queda sin informar. Valida las tres
    /// longitudes antes de escribir ninguna: un valor inválido no deja el contacto a medias.
    /// </summary>
    public void ActualizarContacto(string? nombre, string? telefono, string? correo)
    {
        var nombreNormalizado = NormalizarCampoContacto(nombre, LongitudMaximaContactoNombre, nameof(nombre));
        var telefonoNormalizado = NormalizarCampoContacto(telefono, LongitudMaximaContactoTelefono, nameof(telefono));
        var correoNormalizado = NormalizarCampoContacto(correo, LongitudMaximaContactoCorreo, nameof(correo));
        ContactoNombre = nombreNormalizado;
        ContactoTelefono = telefonoNormalizado;
        ContactoCorreo = correoNormalizado;
    }

    /// <summary>
    /// Valor por defecto del contacto al crear la Visita: el contacto del Centro (<see cref="Centro.Contacto"/>).
    /// Ese campo es texto libre, sin teléfono ni correo separados, así que se copia tal cual al nombre, salvo
    /// cuando el texto entero es una sola dirección de correo, que va al correo (el borrador de «Pedir
    /// prioridad» también reconoce como dirección un contacto de Centro con arroba). No se parte ni se
    /// interpreta nada más. Sin contacto en el Centro, la Visita queda sin contacto.
    /// </summary>
    public void RellenarContactoDesdeCentro(string? contactoCentro)
    {
        var limpio = string.IsNullOrWhiteSpace(contactoCentro) ? null : contactoCentro.Trim();
        if (limpio is null) return;

        if (EsUnaSolaDireccionDeCorreo(limpio) && limpio.Length <= LongitudMaximaContactoCorreo)
            ActualizarContacto(null, null, limpio);
        else
            ActualizarContacto(limpio, null, null);
    }

    private static bool EsUnaSolaDireccionDeCorreo(string texto)
    {
        var arroba = texto.IndexOf('@');
        return arroba > 0 && arroba == texto.LastIndexOf('@') && arroba < texto.Length - 1
            && !texto.Any(char.IsWhiteSpace);
    }

    private static string? NormalizarCampoContacto(string? valor, int longitudMaxima, string parametro)
    {
        var normalizado = string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        if (normalizado?.Length > longitudMaxima)
            throw new ArgumentException($"El campo no puede superar {longitudMaxima} caracteres.", parametro);
        return normalizado;
    }

    /// <summary>
    /// Añadir o quitar un Trabajador cambia la Visita aunque solo se escriba la unión
    /// VisitaTrabajador: renueva la versión para que dos cambios simultáneos choquen (no se
    /// quedan sin Trabajadores entre los dos) y para que un «Editar visita» abierto antes no
    /// guarde encima sin enterarse.
    /// <para>
    /// Además borra la marca de documentación gestionada: lo que se envió o se dio por
    /// gestionado era para quienes entraban entonces, así que la Visita vuelve a «Por
    /// gestionar». El paquete ya enviado no se toca: sigue en su conversación.
    /// </para>
    /// </summary>
    public void RegistrarCambioDeTrabajadores()
    {
        RenovarVersion();
        DocumentacionGestionadaEnUtc = null;
    }

    /// <summary>
    /// Da por gestionada la documentación de la Visita. Marcar otra vez (un segundo envío del
    /// paquete) actualiza la fecha a la última gestión. Lanza si está cancelada: una Visita
    /// cancelada no se modifica, primero se reactiva.
    /// </summary>
    public void MarcarDocumentacionGestionada(DateTime ahoraUtc)
    {
        if (EstaCancelada)
            throw new InvalidOperationException("La visita está cancelada.");

        DocumentacionGestionadaEnUtc = ahoraUtc;
    }

    /// <summary>
    /// Deshace la marca: la Visita vuelve a «Por gestionar». Vale para una marca puesta por
    /// error, venga de la acción manual o del envío del paquete (la Visita no guarda cuál fue);
    /// el paquete ya enviado no se toca. Lanza si está cancelada, igual que al marcar.
    /// </summary>
    public void QuitarMarcaDocumentacionGestionada()
    {
        if (EstaCancelada)
            throw new InvalidOperationException("La visita está cancelada.");

        DocumentacionGestionadaEnUtc = null;
    }

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
    /// papel, el sello no se retira — el margen que tuvo el Gestor CAE para la solicitud
    /// original no deja de haber existido. Devuelve false si ya estaba sellado o si no
    /// hay solicitud de origen que medir. Ese mismo cambio de Trabajadores sí devuelve la
    /// Visita a «Por gestionar» (<see cref="RegistrarCambioDeTrabajadores"/>): el sello mide
    /// la antelación, no el estado de la columna «Documentación».
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

    /// <summary>
    /// Cancela la Visita. Se conserva con sus fechas, trabajadores y notas; solo
    /// deja de estar activa. Lanza si ya estaba cancelada: cancelar dos veces
    /// pisaría la fecha y el motivo de la primera.
    /// </summary>
    public void Cancelar(DateTime ahoraUtc, string? motivo)
    {
        if (EstaCancelada)
            throw new InvalidOperationException("La visita ya está cancelada.");

        // El motivo se valida antes de tocar el estado: un motivo inválido no
        // puede dejar la Visita a medio cancelar.
        var motivoNormalizado = NormalizarMotivo(motivo, nameof(motivo));
        EstaCancelada = true;
        CanceladaEnUtc = ahoraUtc;
        MotivoCancelacion = motivoNormalizado;
        ReactivadaEnUtc = null;
        MotivoReactivacion = null;
    }

    /// <summary>
    /// Deshace la cancelación y devuelve la Visita al estado previo: activa o
    /// finalizada según sus fechas, que cancelar no tocó. Lanza si no estaba cancelada.
    /// </summary>
    public void Reactivar(DateTime ahoraUtc, string? motivo)
    {
        if (!EstaCancelada)
            throw new InvalidOperationException("La visita no está cancelada.");

        var motivoNormalizado = NormalizarMotivo(motivo, nameof(motivo));
        EstaCancelada = false;
        CanceladaEnUtc = null;
        MotivoCancelacion = null;
        ReactivadaEnUtc = ahoraUtc;
        MotivoReactivacion = motivoNormalizado;
    }

    private static string? NormalizarMotivo(string? motivo, string parametro)
    {
        var limpio = string.IsNullOrWhiteSpace(motivo) ? null : motivo.Trim();
        if (limpio is not null && limpio.Length > LongitudMaximaMotivo)
            throw new ArgumentException($"El motivo no puede superar {LongitudMaximaMotivo} caracteres.", parametro);

        return limpio;
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
