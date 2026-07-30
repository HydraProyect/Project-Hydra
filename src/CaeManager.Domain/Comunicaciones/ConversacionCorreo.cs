using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Agregado raíz de la bandeja de correo compartida (ver
/// ARQUITECTURA-INTEGRACIONES.md § 12). ClienteId null significa que el
/// remitente todavía no se ha resuelto contra ningún Cliente — la
/// conversación cae en la cola de triage hasta que alguien la asigna
/// (AsignarCliente). En este vertical slice no lleva ConexionIntegracionId
/// ni HiloExternoId: no hay ingesta real de Microsoft Graph todavía (ver
/// § 12.6), esos campos se incorporan en la siguiente iteración.
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

    private readonly List<MensajeCorreo> _mensajes = [];
    private readonly List<ParticipanteConversacion> _participantes = [];

    public Guid? ClienteId { get; private set; }
    public string Asunto { get; private set; } = string.Empty;
    public EstadoConversacion Estado { get; private set; }
    public Guid? EjecutivoAsignadoId { get; private set; }
    public string? Etiquetas { get; private set; }
    public DateTime FechaUltimoMensajeUtc { get; private set; }

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

    public MensajeCorreo AgregarMensaje(DireccionMensaje direccion, string remitenteEmail, string cuerpoHtml, DateTime? fechaUtc = null)
    {
        var fecha = fechaUtc ?? DateTime.UtcNow;
        var mensaje = new MensajeCorreo(Id, direccion, remitenteEmail, cuerpoHtml, fecha);
        _mensajes.Add(mensaje);

        if (fecha > FechaUltimoMensajeUtc)
            FechaUltimoMensajeUtc = fecha;

        return mensaje;
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
