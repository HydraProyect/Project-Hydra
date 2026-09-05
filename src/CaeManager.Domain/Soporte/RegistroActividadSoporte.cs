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
/// <b>Agrupador XOR (REC-208).</b> Un Actor de Plataforma TALVEG entra a un
/// Tenant ajeno por uno de dos caminos —una Delegación de Tenant de propósito
/// Soporte, o una <see cref="Plataforma.SesionPrivilegiada"/>— y toda
/// actividad tiene que poder colgarse de la visita que la originó, sea cual
/// sea el camino. Este registro admite <b>exactamente uno</b> de
/// <see cref="DelegacionTenantId"/> y <see cref="SesionPrivilegiadaId"/>,
/// nunca los dos ni ninguno; construir uno pasa siempre por
/// <see cref="PorViaHeredada"/> o <see cref="PorSesionPrivilegiada"/>, y la
/// invariante se comprueba en el constructor privado además de en
/// <c>CK_RegistrosActividadSoporte_UnSoloAgrupador</c>
/// (<c>RegistroActividadSoporteConfiguration</c>) — las dos capas, no solo la
/// base, siguiendo el precedente de REC-101 para <c>Documento</c>.
///
/// Aviso: este registro es también dato personal del empleado de soporte
/// (control de actividad laboral). Antes de activarlo hay que informarle,
/// según LOPDGDD arts. 87-90 y ET art. 20.3. Esa es la razón por la que este
/// incremento (REC-208) solo admite <b>de dónde cuelga</b> la actividad —no
/// amplía qué se registra sobre esa actividad.
/// </summary>
public class RegistroActividadSoporte : EntidadConTenant
{
    public const int LongitudMaximaDetalle = 500;

    /// <summary>Quién. Guid suelto hacia ApplicationUser, mismo patrón que el resto del dominio.</summary>
    public Guid UsuarioSoporteId { get; private set; }

    public TipoActividadSoporte Tipo { get; private set; }

    /// <summary>
    /// Agrupador por la vía HEREDADA: la Delegación de Tenant de propósito
    /// Soporte bajo la que ocurrió la actividad. Agrupa todo lo ocurrido en
    /// una misma ventana de acceso, para poder reconstruir una visita
    /// completa en vez de eventos sueltos.
    ///
    /// <para>
    /// Anulable a propósito (REC-208): un Actor de Plataforma TALVEG entra a
    /// un Tenant ajeno por <b>uno de dos caminos</b>, esta Delegación de
    /// Tenant o una <see cref="Plataforma.SesionPrivilegiada"/> — nunca los
    /// dos a la vez ni ninguno. Ver <see cref="SesionPrivilegiadaId"/> y las
    /// factorías <see cref="PorViaHeredada"/>/<see cref="PorSesionPrivilegiada"/>.
    /// </para>
    /// </summary>
    public Guid? DelegacionTenantId { get; private set; }

    /// <summary>
    /// Agrupador por la vía de Sesión Privilegiada (REC-208): el otro camino,
    /// además de la Delegación de Tenant, por el que un Actor de Plataforma
    /// TALVEG entra a un Tenant ajeno. Exactamente uno de los dos agrupadores
    /// está informado — nunca los dos, nunca ninguno — igual que
    /// <see cref="DelegacionTenantId"/>.
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
    /// Registro colgado de una Delegación de Tenant de propósito Soporte —
    /// la vía heredada.
    /// </summary>
    public static RegistroActividadSoporte PorViaHeredada(
        Guid usuarioSoporteId, Guid delegacionTenantId, TipoActividadSoporte tipo, string? detalle = null)
    {
        if (delegacionTenantId == Guid.Empty)
            throw new ArgumentException(
                "El registro de soporte por delegación debe identificar la delegación.", nameof(delegacionTenantId));

        return new RegistroActividadSoporte(usuarioSoporteId, delegacionTenantId, null, tipo, detalle);
    }

    /// <summary>
    /// Registro colgado de una Sesión Privilegiada — el otro camino de
    /// entrada de un Actor de Plataforma TALVEG a un Tenant ajeno (REC-208).
    /// </summary>
    public static RegistroActividadSoporte PorSesionPrivilegiada(
        Guid usuarioSoporteId, Guid sesionPrivilegiadaId, TipoActividadSoporte tipo, string? detalle = null)
    {
        if (sesionPrivilegiadaId == Guid.Empty)
            throw new ArgumentException(
                "El registro de soporte por sesión privilegiada debe identificar la sesión.", nameof(sesionPrivilegiadaId));

        return new RegistroActividadSoporte(usuarioSoporteId, null, sesionPrivilegiadaId, tipo, detalle);
    }

    /// <summary>
    /// Constructor privado: las dos factorías de arriba son la única puerta
    /// pública, y cada una informa exactamente un agrupador y deja el otro en
    /// <c>null</c>. La guarda de aquí (REC-208, mismo patrón que
    /// <c>Documento</c> tras REC-101) es la que hace explícita y comprobable
    /// la invariante <b>"exactamente uno de los dos agrupadores"</b> — sin
    /// ella, un tercer camino de construcción añadido en el futuro (o un
    /// error al tocar las factorías) podría dejarla en manos de la sola
    /// disciplina de quien escriba la siguiente factoría, que es justo el
    /// defecto que REC-101 encontró para <c>Documento</c>: la invariante
    /// vivía solo en PostgreSQL, no en el agregado.
    ///
    /// <c>CK_RegistrosActividadSoporte_UnSoloAgrupador</c>
    /// (<c>RegistroActividadSoporteConfiguration</c>) dice lo mismo en la
    /// base, para lo que no pase por el dominio: siembras, SQL directo,
    /// migraciones futuras.
    /// </summary>
    private RegistroActividadSoporte(
        Guid usuarioSoporteId, Guid? delegacionTenantId, Guid? sesionPrivilegiadaId,
        TipoActividadSoporte tipo, string? detalle)
    {
        if (usuarioSoporteId == Guid.Empty)
            throw new ArgumentException("El registro de soporte debe identificar al usuario.", nameof(usuarioSoporteId));

        var tieneViaHeredada = delegacionTenantId is not null;
        var tieneSesion = sesionPrivilegiadaId is not null;
        if (tieneViaHeredada == tieneSesion)
            throw new ArgumentException(
                "El registro de soporte debe colgar de exactamente uno de sus dos agrupadores posibles " +
                "—una Delegación de Tenant o una Sesión Privilegiada— nunca de los dos a la vez ni de " +
                $"ninguno (CK_RegistrosActividadSoporte_UnSoloAgrupador); tiene {(tieneViaHeredada ? 1 : 0) + (tieneSesion ? 1 : 0)}.");

        UsuarioSoporteId = usuarioSoporteId;
        DelegacionTenantId = delegacionTenantId;
        SesionPrivilegiadaId = sesionPrivilegiadaId;
        Tipo = tipo;
        Detalle = detalle is { Length: > LongitudMaximaDetalle } ? detalle[..LongitudMaximaDetalle] : detalle;
    }
}
