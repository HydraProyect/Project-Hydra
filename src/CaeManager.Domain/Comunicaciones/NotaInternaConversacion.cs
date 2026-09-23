using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Nota interna del equipo en el Unified Timeline de una Conversacion
/// (docs/COMUNICACIONES.md § 12.3, "notas internas"): hablar del caso dentro
/// del caso sin que el interlocutor externo lo vea. <b>Nunca se entrega fuera
/// del Tenant propietario</b> — por eso es una entidad aparte y no un tercer
/// valor de <see cref="DireccionMensaje"/>: todo lo que recorre
/// <see cref="Conversacion.Mensajes"/> (envío por Graph o WhatsApp, búsqueda,
/// clasificadores, contadores de la lista) no puede ver una nota ni por
/// descuido, porque no está en esa colección.
///
/// Sin navegación desde <see cref="Conversacion"/> (mismo criterio que
/// <see cref="EventoConversacion"/>): se da de alta por su propio repositorio.
/// La integridad con el hilo la sostiene una FK compuesta (ConversacionId,
/// TenantId) creada en SQL por la migración, igual que Mensajes y
/// ParticipantesConversacion.
///
/// Texto plano, no HTML: la nota nunca se renderiza como marcado. Sin
/// adjuntos (decisión D1 del MVP de mensajería interna). Inmutable una vez
/// creada: editar y borrar notas no forma parte de este incremento.
/// </summary>
public class NotaInternaConversacion : EntidadConTenant
{
    public const int LongitudMaximaTexto = 4000;

    public Guid ConversacionId { get; private set; }

    /// <summary>Guid suelto hacia ApplicationUser (Infrastructure.Identity) — mismo patrón que NotificacionUsuario.UsuarioDestinatarioId: Domain no puede referenciarlo directamente.</summary>
    public Guid AutorUsuarioId { get; private set; }

    public string Texto { get; private set; } = string.Empty;
    public DateTime FechaUtc { get; private set; }

    private NotaInternaConversacion()
    {
    }

    public NotaInternaConversacion(Guid conversacionId, Guid autorUsuarioId, string texto, DateTime fechaUtc)
    {
        if (conversacionId == Guid.Empty)
            throw new ArgumentException("La nota interna debe pertenecer a una conversación.", nameof(conversacionId));
        if (autorUsuarioId == Guid.Empty)
            throw new ArgumentException("La nota interna debe tener un autor.", nameof(autorUsuarioId));
        if (string.IsNullOrWhiteSpace(texto))
            throw new ArgumentException("La nota interna no puede estar vacía.", nameof(texto));

        var textoLimpio = texto.Trim();
        if (textoLimpio.Length > LongitudMaximaTexto)
            throw new ArgumentException($"La nota interna no puede superar {LongitudMaximaTexto} caracteres.", nameof(texto));

        ConversacionId = conversacionId;
        AutorUsuarioId = autorUsuarioId;
        Texto = textoLimpio;
        FechaUtc = fechaUtc;
    }
}
