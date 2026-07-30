using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Un mensaje individual dentro del hilo de una ConversacionCorreo — mismo
/// patrón de entidad hija con TenantId propio que VisitaTrabajador. En este
/// vertical slice (ver ARQUITECTURA-INTEGRACIONES.md § 12.6) no lleva
/// MensajeExternoId ni adjuntos: no hay ingesta real de Graph todavía.
/// </summary>
public class MensajeCorreo : EntidadConTenant
{
    public Guid ConversacionCorreoId { get; private set; }
    public DireccionMensaje Direccion { get; private set; }
    public string RemitenteEmail { get; private set; } = string.Empty;
    public string CuerpoHtml { get; private set; } = string.Empty;
    public DateTime FechaUtc { get; private set; }

    private MensajeCorreo()
    {
    }

    public MensajeCorreo(Guid conversacionCorreoId, DireccionMensaje direccion, string remitenteEmail, string cuerpoHtml, DateTime fechaUtc)
    {
        if (conversacionCorreoId == Guid.Empty)
            throw new ArgumentException("El mensaje debe pertenecer a una conversación.", nameof(conversacionCorreoId));
        if (string.IsNullOrWhiteSpace(remitenteEmail))
            throw new ArgumentException("El mensaje debe tener un remitente.", nameof(remitenteEmail));
        if (string.IsNullOrWhiteSpace(cuerpoHtml))
            throw new ArgumentException("El mensaje no puede estar vacío.", nameof(cuerpoHtml));

        ConversacionCorreoId = conversacionCorreoId;
        Direccion = direccion;
        RemitenteEmail = remitenteEmail.Trim();
        CuerpoHtml = cuerpoHtml;
        FechaUtc = fechaUtc;
    }
}
