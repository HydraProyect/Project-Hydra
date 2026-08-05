using CaeManager.Domain.Common;

namespace CaeManager.Domain.Integraciones;

/// <summary>
/// Un gestor que participa en el reparto equitativo de una línea WhatsApp en
/// modo PoolInbound. UsuarioId es un Guid suelto hacia ApplicationUser
/// (Infrastructure.Identity) — Domain no puede referenciarlo directamente,
/// mismo patrón que Cliente.EjecutivoUsuarioId.
/// </summary>
public class MiembroPoolLinea : EntidadConTenant
{
    public Guid LineaWhatsAppId { get; private set; }
    public Guid UsuarioId { get; private set; }

    private MiembroPoolLinea()
    {
    }

    public MiembroPoolLinea(Guid lineaWhatsAppId, Guid usuarioId)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El miembro del pool debe ser un usuario.", nameof(usuarioId));

        LineaWhatsAppId = lineaWhatsAppId;
        UsuarioId = usuarioId;
    }
}
