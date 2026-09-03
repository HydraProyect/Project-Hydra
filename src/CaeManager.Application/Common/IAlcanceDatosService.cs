namespace CaeManager.Application.Common;

/// <summary>
/// Resuelve qué Clientes/Centros/Empresas/Subcontratas/Trabajadores/
/// Vehículos puede ver el usuario actual, según su rol (ver Roles.cs en
/// CaeManager.Infrastructure.Identity — Application no puede referenciarlo
/// directamente, así que la implementación real vive en Infrastructure).
///
/// Cada método devuelve null cuando el rol no tiene restricción
/// (Administrador, DireccionCae, Consulta ven todo) — los handlers de
/// Query interpretan null como "no añadir ningún filtro". Un rol
/// restringido (GestorCae, CoordinadorCae, Cliente) sin ningún Cliente
/// visible todavía devuelve una lista VACÍA, nunca null, para que un
/// filtro `Contains` sobre una lista vacía no deje pasar nada por accidente.
///
/// Solo se aplica a las consultas de LISTADO (tablas, Dashboard, Alertas,
/// Reportes) — los selectores de "elige de la base general" (Trabajador,
/// Vehículo: ver ObtenerTrabajadoresParaSelectorQuery/
/// ObtenerVehiculosParaSelectorQuery) se dejan sin restringir a propósito,
/// porque un mismo Trabajador de una Subcontrata puede prestar servicio a
/// varios Clientes de distintos Gestores CAE, y hace falta poder añadirlo
/// a los propios Centros aunque todavía no aparezca en el listado visible.
/// </summary>
public interface IAlcanceDatosService
{
    /// <summary>True para Administrador/DireccionCae/Consulta — sin restricción de cartera.</summary>
    Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Las Empresas visibles <b>desde el lado de gestión CAE</b>: igual que
    /// <see cref="ObtenerEmpresaIdsVisiblesAsync"/> salvo para el rol Cliente
    /// (usuario de portal), que obtiene lista vacía.
    ///
    /// Existe porque la cartera de Empresas se DERIVA de la de Clientes (ver
    /// la implementación): a un contacto de una empresa cliente externa le
    /// salen ahí las contratistas relacionadas con su propio Cliente. Para
    /// LEER documentación eso es correcto —es justo lo que un portal CAE
    /// existe para enseñar—, pero no lo es para los artefactos internos de la
    /// gestión: el hilo de correo con la contratista y el historial de lo que
    /// se le ha reclamado no son contenido de portal.
    ///
    /// Dicho de otra forma: <c>ObtenerEmpresaIdsVisiblesAsync</c> responde
    /// "¿de qué Empresas puede ver datos?", y este responde "¿sobre qué
    /// Empresas opera?". Usar el primero como puerta de autorización de lo
    /// segundo es el error que este método evita.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ObtenerEmpresaIdsParaGestionAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Las Subcontratas visibles <b>desde el lado de gestión CAE</b> — mismo
    /// contrato que <see cref="ObtenerEmpresaIdsParaGestionAsync"/>, para el
    /// mismo motivo: la cartera de Subcontratas también se DERIVA de la de
    /// Clientes (ver <c>ObtenerSubcontrataIdsVisiblesAsync</c>), así que a un
    /// contacto de una empresa cliente externa le salen ahí las subcontratas
    /// de su propio Cliente. Para LEER su documentación eso es correcto; para
    /// artefactos internos de gestión — la credencial de acceso a su portal,
    /// la supervisión operativa — no lo es (REC-159, gemelo de REC-153).
    /// </summary>
    Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken cancellationToken = default);

    /// <summary>Trabajadores con al menos una Asignación activa a un Centro visible — usar solo en listados, no en selectores.</summary>
    Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>Vehículos de una Empresa/Subcontrata visible — usar solo en listados, no en selectores.</summary>
    Task<IReadOnlyList<Guid>?> ObtenerVehiculoIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True si <paramref name="conexionIntegracionId"/> no es un buzón
    /// personal de un gestor (<c>ConexionIntegracion.GestorPropietarioId</c>
    /// null), o si lo es, el usuario actual es su propio dueño. Distinto eje
    /// de autorización que el resto de esta interfaz (cartera de Cliente):
    /// esto es propiedad de un recurso, no pertenencia a una cartera — por
    /// eso no tiene excepción de rol, ni Administrador ve por defecto el
    /// correo personal de otro gestor por aquí (para eso está la gestión
    /// explícita de /integraciones).
    /// </summary>
    Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);
}
