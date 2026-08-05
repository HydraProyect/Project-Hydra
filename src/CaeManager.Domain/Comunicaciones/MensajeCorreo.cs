using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Un mensaje individual dentro del hilo de una ConversacionCorreo — mismo
/// patrón de entidad hija con TenantId propio que VisitaTrabajador.
/// MensajeExternoId (P3-33) es el Message-ID de Graph — clave de
/// idempotencia ante reintentos de notificación de webhook; null en
/// mensajes creados a mano, respondidos sin conexión real, o sembrados como
/// datos de prueba.
/// </summary>
public class MensajeCorreo : EntidadConTenant
{
    public const int LongitudMaximaMensajeExternoId = 300;

    private readonly List<AdjuntoMensajeCorreo> _adjuntos = [];

    public const int LongitudMaximaErrorEntrega = 500;

    public Guid ConversacionCorreoId { get; private set; }
    public DireccionMensaje Direccion { get; private set; }

    /// <summary>En el canal WhatsApp almacena el teléfono E.164 del remitente (deuda nominal — mismo criterio que mantener los nombres *Correo).</summary>
    public string RemitenteEmail { get; private set; } = string.Empty;

    public string CuerpoHtml { get; private set; } = string.Empty;
    public DateTime FechaUtc { get; private set; }
    public string? MensajeExternoId { get; private set; }

    /// <summary>Estado que reporta el webhook statuses[] de WhatsApp — solo mensajes salientes de ese canal; null en el resto.</summary>
    public EstadoEntregaMensaje? EstadoEntrega { get; private set; }

    /// <summary>Motivo del fallo cuando EstadoEntrega == Fallido (errors[].title de Meta).</summary>
    public string? ErrorEntrega { get; private set; }

    public IReadOnlyList<AdjuntoMensajeCorreo> Adjuntos => _adjuntos.AsReadOnly();

    private MensajeCorreo()
    {
    }

    public MensajeCorreo(
        Guid conversacionCorreoId, DireccionMensaje direccion, string remitenteEmail, string cuerpoHtml, DateTime fechaUtc,
        string? mensajeExternoId = null)
    {
        if (conversacionCorreoId == Guid.Empty)
            throw new ArgumentException("El mensaje debe pertenecer a una conversación.", nameof(conversacionCorreoId));
        if (string.IsNullOrWhiteSpace(remitenteEmail))
            throw new ArgumentException("El mensaje debe tener un remitente.", nameof(remitenteEmail));
        if (string.IsNullOrWhiteSpace(cuerpoHtml))
            throw new ArgumentException("El mensaje no puede estar vacío.", nameof(cuerpoHtml));
        if (mensajeExternoId is not null && mensajeExternoId.Length > LongitudMaximaMensajeExternoId)
            throw new ArgumentException($"El Id externo no puede superar {LongitudMaximaMensajeExternoId} caracteres.", nameof(mensajeExternoId));

        ConversacionCorreoId = conversacionCorreoId;
        Direccion = direccion;
        RemitenteEmail = remitenteEmail.Trim();
        CuerpoHtml = cuerpoHtml;
        FechaUtc = fechaUtc;
        MensajeExternoId = mensajeExternoId;
    }

    /// <summary>
    /// Progresión monótona: los webhooks de Meta llegan desordenados y un
    /// "delivered" tardío no debe pisar un "read" ya registrado. Fallido es
    /// terminal. Los estados solo aplican a salientes — un status sobre un
    /// mensaje entrante se ignora en silencio (bug del emisor, no del hilo).
    /// </summary>
    public void ActualizarEstadoEntrega(EstadoEntregaMensaje nuevoEstado, string? error = null)
    {
        if (Direccion != DireccionMensaje.Saliente) return;
        if (EstadoEntrega == EstadoEntregaMensaje.Fallido) return;
        if (EstadoEntrega is { } actual && nuevoEstado != EstadoEntregaMensaje.Fallido && nuevoEstado <= actual) return;

        EstadoEntrega = nuevoEstado;
        ErrorEntrega = nuevoEstado == EstadoEntregaMensaje.Fallido && error is not null
            ? (error.Length > LongitudMaximaErrorEntrega ? error[..LongitudMaximaErrorEntrega] : error)
            : null;
    }

    public AdjuntoMensajeCorreo AgregarAdjunto(string nombreArchivo, string tipoContenido, long tamanoBytes, string archivoUrl)
    {
        var adjunto = new AdjuntoMensajeCorreo(Id, nombreArchivo, tipoContenido, tamanoBytes, archivoUrl);
        _adjuntos.Add(adjunto);
        return adjunto;
    }
}
