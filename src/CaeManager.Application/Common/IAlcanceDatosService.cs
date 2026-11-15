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

    /// <summary>
    /// ¿Alcanza quien mira, en el Tenant actual, el Tenant entero en las ramas operativas? Verdadero con
    /// acceso total o con una Asignación de Cartera de ámbito universal efectiva (la del Gestor CAE o la
    /// de un Gestor a cargo del Coordinador CAE), siempre después de la lente de demo, que solo estrecha.
    /// Falso con una lista explícita de Clientes empresariales, con el rol Cliente o sin nada.
    ///
    /// <para>
    /// No concede nada: es la condición para enseñar un agregado de Tenant (un recuento del Tenant entero,
    /// sin filas) que no se puede recortar por las listas de este servicio, como las bajas del Cuadro de
    /// dirección, que cuentan filas eliminadas que ya no están en ninguna lista. Quien no lo cumple no ve
    /// ese agregado. La implementación por defecto, para los dobles de prueba, se queda en el acceso total:
    /// falla cerrado.
    /// </para>
    /// </summary>
    Task<bool> AlcanzaElTenantEnteroAsync(CancellationToken cancellationToken = default) =>
        TieneAccesoTotalAsync(cancellationToken);

    Task<IReadOnlyList<Guid>?> ObtenerClienteIdsVisiblesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// ¿Quien mira opera <b>desde el lado de gestión CAE</b>? Falso para el rol
    /// Cliente (usuario de portal). Es la misma frontera que trazan
    /// <see cref="ObtenerEmpresaIdsParaGestionAsync"/> y sus gemelos, para un
    /// artefacto interno que no cuelga de una lista sino de una fila que el
    /// usuario de portal SÍ lee: su propio Cliente empresarial está en su
    /// alcance de LECTURA —es lo que le deja abrir su ficha—, pero lo que el
    /// equipo de gestión anota SOBRE él (la «Nota interna», «Solo visible para
    /// tu equipo») no es contenido de portal.
    ///
    /// <b>No es una puerta de alcance y no falla cerrado por sí solo</b>: solo
    /// distingue al rol Cliente del resto, así que sin rol de negocio (Sesión
    /// Privilegiada) responde true. Se pregunta siempre DESPUÉS de comprobar
    /// que la fila es visible (<c>…VisibleAsync</c>), que es quien deniega; a
    /// diferencia de los <c>…ParaGestionAsync</c>, que llevan el alcance dentro.
    /// </summary>
    Task<bool> OperaDesdeElLadoDeGestionAsync(CancellationToken cancellationToken = default);
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
    /// CAE de un canal— no lo es. Tampoco para escribir en el Centro: es la
    /// segunda barrera de la edición y la baja (individual y en lote) del
    /// Centro, de sus canales de gestión documental (alta, edición, baja y
    /// marcar principal) y de su agenda de contactos (guardar y eliminar).
    ///
    /// <para>
    /// <b>Primera de las dos reglas de lectura del rol Consulta sobre el detalle
    /// de gestión</b> (opción C, decisión de Chris del 2026-11-14; DECISION_LOG,
    /// «Tanda 1 de decisiones pendientes», punto 1): la regla <b>operativa</b>.
    /// Este método solo excluye al rol Cliente (usuario de portal). Consulta es
    /// un rol de gestión en solo lectura, no de portal, sea cual sea la vía por
    /// la que la sesión lo tiene (su Tenant de origen, un Tenant beneficiario
    /// delegado por Asignación de Operación o la delegación heredada del acceso
    /// de soporte; decide el rol efectivo), así que recibe
    /// aquí su alcance normal y, con él, la información operativa: el último
    /// motivo de rechazo del historial de rechazos de la acreditación
    /// (<c>UltimoMotivoRechazo</c>), la dirección del portal (<c>UrlAcceso</c>)
    /// y las notas del canal. Para el rol Cliente sigue vigente la decisión del
    /// 2026-11-12 («B: estado, sin motivo»).
    /// La segunda regla, la de trazabilidad interna, se aplica en el servidor
    /// con <c>SituacionDocumentosEnCentrosService.RolesQueLeenReclamaciones</c>
    /// y corta cinco cosas, no solo una: el <b>registro</b> de un cambio de estado
    /// anotado a mano (su nota, su motivo y el Gestor CAE autor), la última
    /// reclamación de los documentos de la situación del Centro, las
    /// reclamaciones sin respuesta de Inicio
    /// (<c>ObtenerReclamacionesSinRespuestaQuery</c>, vacío), la última
    /// reclamación al Cliente del Centro (<c>ObtenerUltimaReclamacionClienteQuery</c>,
    /// null) y el motivo de rechazo de la vista rápida de un Documento
    /// (<c>ObtenerAcreditacionesDelDocumentoQuery</c>, a null). No corta el
    /// motivo de un rechazo en el resto de sus lecturas:
    /// <c>AcreditacionDocumentoPlataforma.CambiarEstadoAMano</c> guarda el de un
    /// rechazo anotado a mano también en el historial de rechazos (causa «Otro»,
    /// sin autor ni nota, sin distinguir su origen del de un rechazo de la
    /// Plataforma CAE), y ese historial sigue llegando a Consulta por la regla
    /// operativa de este método como <c>UltimoMotivoRechazo</c> en
    /// <c>ObtenerDocumentosQuery</c> (solo si la acreditación está rechazada) y en
    /// <c>ObtenerAcreditacionesPorProveedorQuery</c>, que no lo condiciona al
    /// estado actual de la acreditación: la Bandeja del Gestor (y con ella
    /// «Requiere atención» de Inicio), la pestaña Plataformas de Empresa 360 y
    /// <c>GET /extension/acreditaciones-pendientes</c>, que no restringe por
    /// rol; también lo pinta la pestaña Plataforma de Documentos. El DECISION_LOG
    /// solo nombra la vista rápida: para esas otras vías no hay corte decidido
    /// (ver el comentario de esa lista). El historial de reclamaciones enviadas no pasa por esa lista en el
    /// servidor (sus Queries acotan por cartera, no por rol) y se le corta a
    /// Consulta en la presentación (ver el comentario de esa lista).
    /// Añadir Consulta al corte de este método o a esa lista cambia la
    /// regla de producto, no es un ajuste de implementación.
    /// </para>
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
    /// El mismo alcance de gestión que <see cref="ObtenerSubcontrataIdsParaGestionAsync"/>, para una
    /// Subcontrata <b>eliminada</b>: esa lista se deriva de las Empresas vivas y nunca contiene la
    /// fila que <c>RestaurarSubcontrataCommand</c> quiere devolver. Decide por las mismas
    /// coordenadas —rol, cartera y Relaciones Empresariales vigentes, que la baja no toca— sin
    /// pasar por la Empresa. No comprueba que el id sea de una Subcontrata ni de este Tenant: eso
    /// es del comando.
    /// </summary>
    Task<bool> SubcontrataEliminadaParaGestionVisibleAsync(Guid subcontrataId, CancellationToken cancellationToken = default);

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
    /// explícita de /integraciones). Una conexión que no se puede leer en el
    /// Tenant actual (inexistente, de otro Tenant o eliminada) no es visible:
    /// falla en cerrado.
    /// </summary>
    Task<bool> ConexionIntegracionVisibleAsync(Guid conexionIntegracionId, CancellationToken cancellationToken = default);
}
