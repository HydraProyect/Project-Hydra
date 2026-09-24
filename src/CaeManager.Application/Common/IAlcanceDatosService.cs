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
/// Asignación de Cartera universal (decisión del propietario 2026-09-23): un
/// GestorCae con una cartera de ámbito universal vigente sobre el Tenant actual
/// —o un CoordinadorCae con un Gestor a su cargo que la tenga— recibe en TODOS
/// los métodos de rama el Tenant entero, no solo lo que se deriva de sus
/// Clientes empresariales: Centros, Subcontratas, Trabajadores y Vehículos sin
/// Centro, Relación Empresarial ni Asignación que los una a un Cliente. Como
/// listas EXPLÍCITAS, nunca null, y con <c>TieneAccesoTotalAsync</c> en false:
/// es autoridad de Operación, no de Propiedad, y lo que no es operativo
/// (usuarios, configuración, delegaciones) no se decide con estas listas. Las
/// variantes «ParaGestion» siguen el mismo alcance (se ve y se gestiona); el
/// rol Cliente no cambia.
///
/// Lente de demo (<see cref="CaeManager.Application.VistaDemo.IVistaDemoActual"/>): en un entorno
/// de demo con la función activada, una cuenta Administrador/DireccionCae de un Tenant de demo
/// puede pedir la vista «Gestor CAE» de un Gestor concreto. Entonces el alcance devuelto es el
/// real ∩ la cartera de ese Gestor (y <c>TieneAccesoTotalAsync</c> pasa a false). La lente solo
/// ESTRECHA —nunca amplía— y, como estos métodos también los consultan los comandos, un comando
/// ve el mismo alcance estrechado que un Gestor real: solo puede ser más estricto, jamás menos.
///
/// Se aplica a las consultas de LISTADO (tablas, Dashboard, Alertas,
/// Reportes) y a los selectores que cuelgan algo de una entidad ya existente
/// (Documento, Gestión, Proyecto...). La excepción son los selectores de
/// Trabajador que piden explícitamente la base general
/// (AlcanceSelectorTrabajadores.BaseGeneralDelTenant, ver allí sus
/// consumidores): el de Asignación masiva se deja sin restringir a propósito,
/// porque un mismo Trabajador de una Subcontrata puede prestar servicio a
/// Clientes empresariales de distintos Gestores CAE, y hace falta poder crear
/// su primera Asignación aunque todavía no aparezca en la cartera visible.
/// </summary>
public interface IAlcanceDatosService
{
    /// <summary>True para Administrador/DireccionCae/Consulta — sin restricción de cartera.</summary>
    Task<bool> TieneAccesoTotalAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Guid>?> ObtenerCentroIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Los Centros sobre los que se <b>opera desde el lado de gestión CAE</b>:
    /// igual que <see cref="ObtenerCentroIdsVisiblesAsync"/> salvo para el rol
    /// Cliente (usuario de portal), que obtiene lista vacía. Mismo motivo que
    /// <see cref="ObtenerEmpresaIdsParaGestionAsync"/> (REC-153): la cartera de
    /// Centros se DERIVA de la de Clientes, así que a un contacto de una
    /// empresa cliente externa le salen sus propios Centros. Para LEER su
    /// estado eso es correcto; para un artefacto interno de gestión —el
    /// usuario y la contraseña con los que se entra al portal de la Plataforma
    /// CAE de un canal— no lo es.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ObtenerCentroIdsParaGestionAsync(CancellationToken cancellationToken = default);
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
    /// un artefacto interno de gestión —la credencial de acceso a su portal
    /// (REC-159) y los siete comandos de escritura de Subcontrata (REC-172,
    /// gemelo de REC-149 en Empresa: alta, edición, cambio de nivel de
    /// servicio, baja individual y en lote, y las verificaciones externas)—
    /// no lo es. No confundir con la vista de supervisión (checklist de
    /// cumplimiento documental): esa se midió al cerrar REC-159 y se queda en
    /// el alcance de LECTURA a propósito, por ser justo la documentación que
    /// el portal existe para enseñar.
    /// </summary>
    Task<IReadOnlyList<Guid>?> ObtenerSubcontrataIdsParaGestionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Trabajadores con al menos una Asignación activa a un Centro visible y, para un Gestor o
    /// Coordinador CAE con cartera, además toda la plantilla de la Empresa propia del Tenant aunque
    /// no tenga ninguna Asignación. Lo usan los listados y los selectores con alcance
    /// <c>AlcanceSelectorTrabajadores.Cartera</c>; los selectores de base general del Tenant no
    /// pasan por aquí (ver <c>AlcanceSelectorTrabajadores</c>).
    /// </summary>
    Task<IReadOnlyList<Guid>?> ObtenerTrabajadorIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>Vehículos de una Empresa/Subcontrata visible — listados y selectores.</summary>
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
