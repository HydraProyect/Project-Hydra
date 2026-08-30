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
    /// Agrupador de la vía HEREDADA. Nullable desde B2: los registros nuevos
    /// cuelgan de una sesión privilegiada, no de una delegación.
    ///
    /// <para>
    /// No se retira ni se reescribe el histórico: sigue siendo la única forma
    /// de consultar las visitas anteriores a B, y su índice se conserva.
    /// </para>
    /// </summary>
    public Guid? DelegacionTenantId { get; private set; }

    /// <summary>
    /// Agrupador de la vía NUEVA: la sesión privilegiada por la que se abrió
    /// el contexto. Guid suelto, mismo patrón que <see cref="DelegacionTenantId"/>.
    /// </summary>
    public Guid? SesionPrivilegiadaId { get; private set; }

    /// <summary>Ruta de la pantalla, o identificación del elemento con el que se interactuó.</summary>
    public string? Detalle { get; private set; }

    public DateTime OcurridaEnUtc { get; private set; } = DateTime.UtcNow;

    private RegistroActividadSoporte()
    {
        // Requerido por EF Core.
    }

    /// <summary>
    /// Registro de la vía heredada, colgado de una delegación de soporte.
    /// Se conserva mientras B3 no retire esa vía: durante B1 y B2 <b>conviven
    /// las dos</b>, que es lo que garantiza no quedarse sin soporte a mitad de
    /// la migración.
    /// </summary>
    public static RegistroActividadSoporte PorDelegacion(
        Guid usuarioSoporteId, Guid delegacionTenantId, TipoActividadSoporte tipo, string? detalle = null)
    {
        if (delegacionTenantId == Guid.Empty)
            throw new ArgumentException(
                "El registro de soporte por delegación debe identificar la delegación.", nameof(delegacionTenantId));

        return new RegistroActividadSoporte(usuarioSoporteId, tipo, detalle)
        {
            DelegacionTenantId = delegacionTenantId
        };
    }

    /// <summary>
    /// Registro de la vía nueva, colgado de una sesión privilegiada.
    /// </summary>
    public static RegistroActividadSoporte PorSesionPrivilegiada(
        Guid usuarioSoporteId, Guid sesionPrivilegiadaId, TipoActividadSoporte tipo, string? detalle = null)
    {
        if (sesionPrivilegiadaId == Guid.Empty)
            throw new ArgumentException(
                "El registro de soporte por sesión debe identificar la sesión.", nameof(sesionPrivilegiadaId));

        return new RegistroActividadSoporte(usuarioSoporteId, tipo, detalle)
        {
            SesionPrivilegiadaId = sesionPrivilegiadaId
        };
    }

    /// <summary>
    /// Constructor privado: obliga a pasar por una de las dos fábricas, que son
    /// las únicas que informan un agrupador.
    ///
    /// <para>
    /// <b>Por qué no un constructor público con los dos nullables.</b> Un
    /// registro de actividad de soporte sin agrupador no es reconstruible: no
    /// se puede saber a qué visita pertenece, y una traza que no se puede
    /// agrupar no responde a la pregunta para la que existe —«enséñame todo lo
    /// que hizo soporte en esta visita»—. Dejando el constructor privado, ese
    /// estado es <b>irrepresentable</b> en vez de estar solo desaconsejado. La
    /// restricción <c>CK_RegistrosActividadSoporte_UnSoloAgrupador</c> dice lo
    /// mismo en la base, para lo que no pase por el dominio.
    /// </para>
    /// </summary>
    private RegistroActividadSoporte(Guid usuarioSoporteId, TipoActividadSoporte tipo, string? detalle)
    {
        if (usuarioSoporteId == Guid.Empty)
            throw new ArgumentException("El registro de soporte debe identificar al usuario.", nameof(usuarioSoporteId));

        UsuarioSoporteId = usuarioSoporteId;
        Tipo = tipo;
        Detalle = detalle is { Length: > LongitudMaximaDetalle } ? detalle[..LongitudMaximaDetalle] : detalle;
    }
}
