using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Agregado raíz de la bandeja de correo compartida (ver
/// ARQUITECTURA-INTEGRACIONES.md § 12). ClienteId null significa que el
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
/// diseño aprobado pide que ConversacionCorreo controle el alta de sus
/// mensajes y participantes como una operación de negocio única (ver
/// ConversacionCorreoConfiguration para cómo EF Core materializa estos
/// campos privados).
/// </summary>
public class ConversacionCorreo : EntidadBase
{
    public const int LongitudMaximaAsunto = 300;
    public const int LongitudMaximaEtiquetas = 500;
    public const int LongitudMaximaHiloExternoId = 300;

    private readonly List<MensajeCorreo> _mensajes = [];
    private readonly List<ParticipanteConversacion> _participantes = [];

    public Guid? ClienteId { get; private set; }
    public string Asunto { get; private set; } = string.Empty;
    public EstadoConversacion Estado { get; private set; }
    public Guid? EjecutivoAsignadoId { get; private set; }
    public string? Etiquetas { get; private set; }
    public DateTime FechaUltimoMensajeUtc { get; private set; }
    public Guid? ConexionIntegracionId { get; private set; }
    public string? HiloExternoId { get; private set; }

    public IReadOnlyList<MensajeCorreo> Mensajes => _mensajes.AsReadOnly();
    public IReadOnlyList<ParticipanteConversacion> Participantes => _participantes.AsReadOnly();

    private ConversacionCorreo()
    {
    }

    public ConversacionCorreo(string asunto, Guid? clienteId = null, string? etiquetas = null)
    {
        EstablecerAsunto(asunto);
        EstablecerEtiquetas(etiquetas);
        ClienteId = clienteId;
        Estado = EstadoConversacion.Abierta;
        FechaUltimoMensajeUtc = DateTime.UtcNow;
    }

    public MensajeCorreo AgregarMensaje(
        DireccionMensaje direccion, string remitenteEmail, string cuerpoHtml, DateTime? fechaUtc = null, string? mensajeExternoId = null)
    {
        var fecha = fechaUtc ?? DateTime.UtcNow;
        var mensaje = new MensajeCorreo(Id, direccion, remitenteEmail, cuerpoHtml, fecha, mensajeExternoId);
        _mensajes.Add(mensaje);

        if (fecha > FechaUltimoMensajeUtc)
            FechaUltimoMensajeUtc = fecha;

        return mensaje;
    }

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
