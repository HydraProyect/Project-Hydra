using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Agregado raíz de la bandeja compartida multicanal — correo y WhatsApp
/// (ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md § 12; renombrado desde
/// ConversacionCorreo en el paso 0 del rediseño, Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md
/// § 16.2). ClienteId null significa que el
/// remitente todavía no se ha resuelto contra ningún Cliente — la
/// conversación cae en la cola de triage hasta que alguien la asigna
/// (AsignarCliente). ConexionIntegracionId/HiloExternoId (P3-33, la
/// "siguiente iteración" que § 12.6 dejaba planteada) identifican qué buzón
/// conectado atiende el hilo y el conversationId de Graph para el threading
/// — null en conversaciones creadas a mano o sembradas como datos de
/// prueba, nunca en una ingerida por webhook real.
///
/// Mensajes/Participantes se exponen como colecciones de solo lectura sobre
/// un campo privado — patrón nuevo en este repositorio (el resto de
/// agregados con entidades hijas, p. ej. Visita/VisitaTrabajador, las
/// gestionan por repositorio aparte sin navegación) porque aquí el propio
/// diseño aprobado pide que Conversacion controle el alta de sus
/// mensajes y participantes como una operación de negocio única (ver
/// ConversacionConfiguration para cómo EF Core materializa estos
/// campos privados).
/// </summary>
public class Conversacion : EntidadBase
{
    public const int LongitudMaximaAsunto = 300;
    public const int LongitudMaximaEtiquetas = 500;
    public const int LongitudMaximaHiloExternoId = 300;
    public const int LongitudMaximaTelefonoContacto = 20;

    /// <summary>Ventana de servicio de WhatsApp (Meta): fuera de las 24 h desde el último entrante solo se pueden enviar plantillas aprobadas.</summary>
    public static readonly TimeSpan DuracionVentanaServicio = TimeSpan.FromHours(24);

    /// <summary>
    /// Plazo que una conversación Resuelta espera antes de cerrarse sola (decisión de producto 2026-10-17, hueco 2 de
    /// Comunicaciones). Son 4 días naturales (96 horas) desde que se resolvió, no laborables: el reloj es el del hilo,
    /// no el calendario del Gestor CAE. Un mensaje entrante antes de que se cumpla la reabre y reinicia la cuenta.
    /// </summary>
    public static readonly TimeSpan PlazoCierreAutomaticoTrasResolucion = TimeSpan.FromDays(4);

    private readonly List<Mensaje> _mensajes = [];
    private readonly List<ParticipanteConversacion> _participantes = [];
    private readonly List<CambioEstadoConversacion> _cambiosEstado = [];

    public Guid? ClienteId { get; private set; }

    /// <summary>
    /// Empresa contraparte a la que pertenece el hilo cuando el
    /// interlocutor no es un Cliente sino la Empresa titular de su propia
    /// documentación — hoy solo lo pone la reclamación de ámbito Empresa
    /// (DEC-7). Es un ancla de ALCANCE, no un segundo "cliente": existe
    /// para que el hilo se acote a quien tiene esa Empresa en cartera en vez
    /// de caer en la cola de triage, que por definición es compartida
    /// (ver § 12.4 y AlcanceDatosServiceExtensions.ConversacionVisibleAsync).
    ///
    /// Excluyente con <see cref="ClienteId"/>: un hilo tiene como mucho un
    /// ancla. Con las dos en null sigue significando lo de siempre —
    /// remitente todavía sin resolver, cola de triage.
    /// </summary>
    public Guid? EmpresaId { get; private set; }

    public string Asunto { get; private set; } = string.Empty;
    public EstadoConversacion Estado { get; private set; }

    /// <summary>
    /// Instante en que la conversación entró en su <see cref="Estado"/> actual. Es la copia desnormalizada del último
    /// <see cref="CambiosEstado"/> para poder preguntar «Resuelta desde hace más de N días» sin cargar el historial
    /// (el cierre automático lo usa) y para decidir si un mensaje entrante llegó antes o después del cambio.
    /// </summary>
    public DateTime EstadoDesdeUtc { get; private set; }

    public Guid? EjecutivoAsignadoId { get; private set; }
    public string? Etiquetas { get; private set; }
    public DateTime FechaUltimoMensajeUtc { get; private set; }
    public Guid? ConexionIntegracionId { get; private set; }
    public string? HiloExternoId { get; private set; }
    public CanalConversacion Canal { get; private set; }

    /// <summary>Teléfono E.164 del contacto — solo canal WhatsApp. El "hilo" WhatsApp es (ConexionIntegracionId, TelefonoContacto) + estado, no HiloExternoId (su índice único impediría reabrir conversación con el mismo teléfono tras cerrar la anterior).</summary>
    public string? TelefonoContacto { get; private set; }

    /// <summary>Fecha del último mensaje ENTRANTE — ancla de la ventana de servicio de 24 h de Meta (canal WhatsApp).</summary>
    public DateTime? FechaUltimoMensajeEntranteUtc { get; private set; }

    public IReadOnlyList<Mensaje> Mensajes => _mensajes.AsReadOnly();
    public IReadOnlyList<ParticipanteConversacion> Participantes => _participantes.AsReadOnly();

    /// <summary>
    /// Historial de cambios de estado, del más antiguo al más reciente. Solo anexado y sin autor (ver
    /// <see cref="CambioEstadoConversacion"/>). No lo carga <c>ObtenerPorIdAsync</c>: lo que lo necesita (las métricas
    /// de tiempo) lo consulta aparte.
    /// </summary>
    public IReadOnlyList<CambioEstadoConversacion> CambiosEstado => _cambiosEstado.AsReadOnly();

    private Conversacion()
    {
    }

    /// <param name="empresaId">
    /// Ancla de Empresa contraparte, excluyente con <paramref name="clienteId"/>
    /// — ver <see cref="EmpresaId"/>. Solo la reclamación de ámbito Empresa
    /// la usa hoy.
    /// </param>
    public Conversacion(string asunto, Guid? clienteId = null, string? etiquetas = null, Guid? empresaId = null)
    {
        if (clienteId is not null && empresaId is not null)
            throw new ArgumentException("Una conversación se ancla a un Cliente o a una Empresa, nunca a los dos.", nameof(empresaId));

        EstablecerAsunto(asunto);
        EstablecerEtiquetas(etiquetas);
        ClienteId = clienteId;
        EmpresaId = empresaId;
        Estado = EstadoConversacion.Abierta;
        Canal = CanalConversacion.Correo;
        FechaUltimoMensajeUtc = DateTime.UtcNow;
        EstadoDesdeUtc = FechaUltimoMensajeUtc;
    }

    /// <summary>
    /// Crea una conversación del canal WhatsApp — solo la llama el flujo de
    /// ingesta del webhook. El ejecutivo llega ya resuelto por el
    /// enrutamiento híbrido (contacto conocido→gestor de cartera, o modo de
    /// la línea); null solo si la línea quedó mal configurada (pool vacío).
    /// </summary>
    public static Conversacion CrearWhatsApp(
        string telefonoContacto, Guid conexionIntegracionId, Guid? clienteId, Guid? ejecutivoId)
    {
        if (string.IsNullOrWhiteSpace(telefonoContacto))
            throw new ArgumentException("La conversación WhatsApp debe tener el teléfono del contacto.", nameof(telefonoContacto));
        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La conversación WhatsApp debe pertenecer a una conexión.", nameof(conexionIntegracionId));

        var telefono = telefonoContacto.Trim();
        if (telefono.Length > LongitudMaximaTelefonoContacto)
            throw new ArgumentException($"El teléfono no puede superar {LongitudMaximaTelefonoContacto} caracteres.", nameof(telefonoContacto));

        var conversacion = new Conversacion($"WhatsApp {telefono}", clienteId)
        {
            Canal = CanalConversacion.WhatsApp,
            TelefonoContacto = telefono,
            ConexionIntegracionId = conexionIntegracionId,
            EjecutivoAsignadoId = ejecutivoId
        };
        return conversacion;
    }

    /// <summary>
    /// <paramref name="canal"/> es explícito a propósito — cada mensaje conoce
    /// su propio canal (paso 1 del rediseño, Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 16.1),
    /// aunque hoy todo llamador pase <see cref="Canal"/> de esta misma
    /// conversación (todavía no hay hilos mixtos). No se infiere de
    /// <see cref="Canal"/> para no ocultar la decisión el día que un mensaje
    /// pueda viajar por un canal distinto al de creación del hilo.
    /// </summary>
    public Mensaje AgregarMensaje(
        DireccionMensaje direccion, CanalConversacion canal, string remitente, string cuerpoHtml,
        DateTime? fechaUtc = null, string? mensajeExternoId = null)
    {
        var fecha = fechaUtc ?? DateTime.UtcNow;
        var mensaje = new Mensaje(Id, direccion, canal, remitente, cuerpoHtml, fecha, mensajeExternoId);
        _mensajes.Add(mensaje);

        if (fecha > FechaUltimoMensajeUtc)
            FechaUltimoMensajeUtc = fecha;

        if (direccion == DireccionMensaje.Entrante &&
            (FechaUltimoMensajeEntranteUtc is null || fecha > FechaUltimoMensajeEntranteUtc))
            FechaUltimoMensajeEntranteUtc = fecha;

        ReabrirSiLlegaRespuesta(direccion, fecha);

        return mensaje;
    }

    /// <summary>
    /// Una respuesta del interlocutor reabre sola la conversación que dimos por resuelta y la que estaba esperando
    /// precisamente esa respuesta (decisión 2026-10-17). Solo si el mensaje es posterior al cambio de estado: uno
    /// atrasado —ingesta tardía de un correo anterior a la resolución— no deshace lo que el Gestor CAE ya resolvió.
    /// <see cref="EstadoConversacion.EnEsperaDeTercero"/> no se mueve: un entrante no dice si es de la Empresa gestionada
    /// o del tercero. <see cref="EstadoConversacion.Cerrada"/> tampoco: reabrirla es de una persona.
    /// La fecha del mensaje la pone el proveedor; si viniese del futuro por desfase de reloj, el cambio se registra con
    /// la hora actual para que el historial no deje de ser monótono.
    /// </summary>
    private void ReabrirSiLlegaRespuesta(DireccionMensaje direccion, DateTime fechaMensajeUtc)
    {
        if (direccion != DireccionMensaje.Entrante
            || Estado is not (EstadoConversacion.Resuelta or EstadoConversacion.EsperandoRespuestaExterna)
            || fechaMensajeUtc <= EstadoDesdeUtc)
            return;

        var ahora = DateTime.UtcNow;
        RegistrarCambioDeEstado(EstadoConversacion.Abierta, fechaMensajeUtc > ahora ? ahora : fechaMensajeUtc);
    }

    /// <summary>
    /// True si aún puede enviarse un mensaje libre (texto/imagen) por WhatsApp:
    /// Meta solo lo permite dentro de las 24 h siguientes al último mensaje
    /// entrante del contacto; fuera de la ventana solo plantillas aprobadas
    /// (no soportadas en v1 — el envío se bloquea).
    /// </summary>
    public bool VentanaServicioAbierta(DateTime ahoraUtc) =>
        Canal == CanalConversacion.WhatsApp &&
        FechaUltimoMensajeEntranteUtc is { } ultimoEntrante &&
        ahoraUtc - ultimoEntrante < DuracionVentanaServicio;

    /// <summary>
    /// Ata el hilo a un buzón conectado (P3-33) — se llama en el flujo de
    /// ingesta de webhook y en el envío de un mensaje nuevo saliente.
    /// </summary>
    public void AsociarConexion(Guid conexionIntegracionId, string hiloExternoId)
    {
        if (conexionIntegracionId == Guid.Empty)
            throw new ArgumentException("La conexión no puede estar vacía.", nameof(conexionIntegracionId));
        if (string.IsNullOrWhiteSpace(hiloExternoId))
            throw new ArgumentException("El hilo externo no puede estar vacío.", nameof(hiloExternoId));

        var normalizado = hiloExternoId.Trim();
        if (normalizado.Length > LongitudMaximaHiloExternoId)
            throw new ArgumentException($"El hilo externo no puede superar {LongitudMaximaHiloExternoId} caracteres.", nameof(hiloExternoId));

        ConexionIntegracionId = conexionIntegracionId;
        HiloExternoId = normalizado;
    }

    public ParticipanteConversacion AgregarParticipante(
        string email, RolParticipante rol, TipoParticipanteOrigen tipoOrigen, Guid? entidadRelacionadaId = null)
    {
        var participante = new ParticipanteConversacion(Id, email, rol, tipoOrigen, entidadRelacionadaId);
        _participantes.Add(participante);
        return participante;
    }

    /// <summary>
    /// Cambia el estado si <see cref="TransicionesEstadoConversacion"/> lo permite; si no, devuelve el fallo de dominio
    /// y no toca nada. Registra el instante del cambio en <see cref="CambiosEstado"/> — nunca quién lo hizo.
    /// <paramref name="ahoraUtc"/> existe para que los tests fijen el reloj; en producción es el instante actual.
    /// </summary>
    public Result CambiarEstado(EstadoConversacion nuevoEstado, DateTime? ahoraUtc = null)
    {
        if (!TransicionesEstadoConversacion.EstaPermitida(Estado, nuevoEstado))
            return Result.Fallo(TransicionesEstadoConversacion.ErrorTransicionNoPermitida(Estado, nuevoEstado));

        RegistrarCambioDeEstado(nuevoEstado, ahoraUtc ?? DateTime.UtcNow);
        return Result.Exito();
    }

    /// <summary>
    /// Cierra la conversación si lleva Resuelta al menos <see cref="PlazoCierreAutomaticoTrasResolucion"/>. Lo llama el
    /// proceso programado de cierre (Sistema); devuelve si la cerró. El plazo se cuenta desde <see cref="EstadoDesdeUtc"/>:
    /// reabrir y volver a resolver lo reinicia.
    /// </summary>
    public bool CerrarSiResueltaHaceMasDelPlazo(DateTime ahoraUtc)
    {
        if (Estado != EstadoConversacion.Resuelta || ahoraUtc - EstadoDesdeUtc < PlazoCierreAutomaticoTrasResolucion)
            return false;

        RegistrarCambioDeEstado(EstadoConversacion.Cerrada, ahoraUtc);
        return true;
    }

    private void RegistrarCambioDeEstado(EstadoConversacion nuevoEstado, DateTime fechaUtc)
    {
        _cambiosEstado.Add(new CambioEstadoConversacion(Id, Estado, nuevoEstado, fechaUtc));
        Estado = nuevoEstado;
        EstadoDesdeUtc = fechaUtc;
    }

    public void Asignar(Guid? ejecutivoId) => EjecutivoAsignadoId = ejecutivoId;

    /// <summary>Resuelve el triage: asigna un Cliente real a una conversación que llegó sin resolver.</summary>
    public void AsignarCliente(Guid clienteId)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El cliente asignado no puede estar vacío.", nameof(clienteId));

        // Resolver el remitente como Cliente retira el ancla de Empresa si la
        // había: el hilo pasa a acotarse por cartera de Cliente, y dejar las
        // dos puestas rompería la exclusión que el constructor impone.
        ClienteId = clienteId;
        EmpresaId = null;
    }

    /// <summary>
    /// Fusiona los mensajes de <paramref name="origen"/> en este hilo —
    /// resultado de una vinculación confirmada por el gestor tras el
    /// Conversation Matching Engine (Project-Hydra-Negocio/tecnico/docs/COMUNICACIONES.md § 13.2). Nunca
    /// automático: solo <c>VincularConversacionCommandHandler</c> la llama,
    /// después de que el gestor confirma la propuesta. Cada mensaje conserva
    /// su propio <see cref="Mensaje.Canal"/> — con esto nace un hilo mixto de
    /// verdad (§ 13.1). <paramref name="origen"/> queda sin mensajes propios;
    /// el llamador es responsable de marcarla <see cref="EstadoConversacion.Cerrada"/>.
    /// Los Participantes de <paramref name="origen"/> no se trasladan: hoy
    /// WhatsApp (el único canal que hace de origen en este flujo) nunca los
    /// puebla (§ 14, hueco ya identificado), así que no hay nada que perder.
    /// </summary>
    public void AbsorberMensajesDe(Conversacion origen)
    {
        if (origen.Id == Id)
            throw new InvalidOperationException("Una conversación no puede fusionarse consigo misma.");

        foreach (var mensaje in origen._mensajes)
        {
            mensaje.ReasignarConversacion(Id);
            _mensajes.Add(mensaje);

            if (mensaje.FechaUtc > FechaUltimoMensajeUtc)
                FechaUltimoMensajeUtc = mensaje.FechaUtc;

            if (mensaje.Direccion == DireccionMensaje.Entrante &&
                (FechaUltimoMensajeEntranteUtc is null || mensaje.FechaUtc > FechaUltimoMensajeEntranteUtc))
                FechaUltimoMensajeEntranteUtc = mensaje.FechaUtc;

            // Un mensaje entrante que llega por vinculación vale como cualquier otro entrante: si este hilo esperaba
            // esa respuesta, se reabre.
            ReabrirSiLlegaRespuesta(mensaje.Direccion, mensaje.FechaUtc);
        }

        origen._mensajes.Clear();
    }

    private void EstablecerAsunto(string asunto)
    {
        if (string.IsNullOrWhiteSpace(asunto))
            throw new ArgumentException("La conversación debe tener un asunto.", nameof(asunto));

        var normalizado = asunto.Trim();
        if (normalizado.Length > LongitudMaximaAsunto)
            throw new ArgumentException($"El asunto no puede superar {LongitudMaximaAsunto} caracteres.", nameof(asunto));

        Asunto = normalizado;
    }

    private void EstablecerEtiquetas(string? etiquetas)
    {
        if (etiquetas is not null && etiquetas.Length > LongitudMaximaEtiquetas)
            throw new ArgumentException($"Las etiquetas no pueden superar {LongitudMaximaEtiquetas} caracteres.", nameof(etiquetas));

        Etiquetas = etiquetas;
    }
}
