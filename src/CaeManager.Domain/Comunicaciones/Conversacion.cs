using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Agregado raíz de la bandeja compartida multicanal — correo y WhatsApp
/// (ver ARQUITECTURA-INTEGRACIONES.md § 12; renombrado desde
/// ConversacionCorreo en el paso 0 del rediseño, docs/COMUNICACIONES.md
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

    private readonly List<Mensaje> _mensajes = [];
    private readonly List<ParticipanteConversacion> _participantes = [];

    public Guid? ClienteId { get; private set; }
    public string Asunto { get; private set; } = string.Empty;
    public EstadoConversacion Estado { get; private set; }
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

    private Conversacion()
    {
    }

    public Conversacion(string asunto, Guid? clienteId = null, string? etiquetas = null)
    {
        EstablecerAsunto(asunto);
        EstablecerEtiquetas(etiquetas);
        ClienteId = clienteId;
        Estado = EstadoConversacion.Abierta;
        Canal = CanalConversacion.Correo;
        FechaUltimoMensajeUtc = DateTime.UtcNow;
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

    public Mensaje AgregarMensaje(
        DireccionMensaje direccion, string remitente, string cuerpoHtml, DateTime? fechaUtc = null, string? mensajeExternoId = null)
    {
        var fecha = fechaUtc ?? DateTime.UtcNow;
        var mensaje = new Mensaje(Id, direccion, remitente, cuerpoHtml, fecha, mensajeExternoId);
        _mensajes.Add(mensaje);

        if (fecha > FechaUltimoMensajeUtc)
            FechaUltimoMensajeUtc = fecha;

        if (direccion == DireccionMensaje.Entrante &&
            (FechaUltimoMensajeEntranteUtc is null || fecha > FechaUltimoMensajeEntranteUtc))
            FechaUltimoMensajeEntranteUtc = fecha;

        return mensaje;
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
    /// Ata el hilo a un buzón conectado (P3-33) — solo la llama el flujo de
    /// ingesta de webhook, nunca el alta manual ni el seeder de demo.
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

    public void CambiarEstado(EstadoConversacion nuevoEstado) => Estado = nuevoEstado;

    public void Asignar(Guid? ejecutivoId) => EjecutivoAsignadoId = ejecutivoId;

    /// <summary>Resuelve el triage: asigna un Cliente real a una conversación que llegó sin resolver.</summary>
    public void AsignarCliente(Guid clienteId)
    {
        if (clienteId == Guid.Empty)
            throw new ArgumentException("El cliente asignado no puede estar vacío.", nameof(clienteId));

        ClienteId = clienteId;
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
