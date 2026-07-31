using CaeManager.Domain.Common;

namespace CaeManager.Domain.Soporte;

/// <summary>
/// Rastro de lo que hace el equipo de Hydra mientras opera el tenant de un
/// cliente en una sesión de soporte (decisión del propietario del producto,
/// 2026-07-31: trazabilidad completa, incluidas navegación e interacciones).
///
/// <b>Solo se escribe durante sesiones de soporte</b>, no para el uso normal
/// de la aplicación. Es lo que hace viable registrar a este nivel de detalle
/// sin castigar a la base de datos: el volumen no depende de cuántos usuarios
/// tenga el producto, sino de cuántas incidencias se atienden.
///
/// Lleva <c>TenantId</c> con filtro global como todo lo demás, y ese tenant es
/// el <b>del cliente visitado</b>, no el de Hydra: el registro pertenece a
/// quien responde de esos datos, y así el propio cliente puede consultarlo —
/// que es el sentido de rendir cuentas.
///
/// Aviso: este registro es también dato personal del empleado de soporte
/// (control de actividad laboral). Antes de activarlo hay que informarle,
/// según LOPDGDD arts. 87-90 y ET art. 20.3.
/// </summary>
public class RegistroActividadSoporte : EntidadConTenant
{
    public const int LongitudMaximaDetalle = 500;

    /// <summary>Quién. Guid suelto hacia ApplicationUser, mismo patrón que el resto del dominio.</summary>
    public Guid UsuarioSoporteId { get; private set; }

    public TipoActividadSoporte Tipo { get; private set; }

    /// <summary>
    /// Agrupa todo lo ocurrido en una misma ventana de acceso, para poder
    /// reconstruir una visita completa en vez de eventos sueltos.
    /// </summary>
    public Guid DelegacionTenantId { get; private set; }

    /// <summary>Ruta de la pantalla, o identificación del elemento con el que se interactuó.</summary>
    public string? Detalle { get; private set; }

    public DateTime OcurridaEnUtc { get; private set; } = DateTime.UtcNow;

    private RegistroActividadSoporte()
    {
        // Requerido por EF Core.
    }

    public RegistroActividadSoporte(
        Guid usuarioSoporteId, Guid delegacionTenantId, TipoActividadSoporte tipo, string? detalle = null)
    {
        if (usuarioSoporteId == Guid.Empty)
            throw new ArgumentException("El registro de soporte debe identificar al usuario.", nameof(usuarioSoporteId));
        if (delegacionTenantId == Guid.Empty)
            throw new ArgumentException("El registro de soporte debe colgar de una delegación.", nameof(delegacionTenantId));

        UsuarioSoporteId = usuarioSoporteId;
        DelegacionTenantId = delegacionTenantId;
        Tipo = tipo;
        Detalle = detalle is { Length: > LongitudMaximaDetalle } ? detalle[..LongitudMaximaDetalle] : detalle;
    }
}
