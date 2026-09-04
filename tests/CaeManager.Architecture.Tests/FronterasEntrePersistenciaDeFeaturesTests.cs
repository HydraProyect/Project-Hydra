using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Horizonte 2.5 (MACRO_PLAN_2026-08-13.md § 2.5, regla 1): el código está
/// organizado por feature bajo <c>src/CaeManager.Application/&lt;Feature&gt;/</c>,
/// cada una dueña en principio de sus propias interfaces de persistencia
/// (<c>I*QueryContext</c> del lado de lectura en Application, <c>I*Repository</c>
/// del lado de escritura en Domain). En la práctica hay muchísimas referencias
/// cruzadas legítimas — sobre todo query handlers que componen una vista a
/// partir de varios contextos de lectura (p. ej. <c>ObtenerVisitasQueryHandler</c>
/// necesita Centros/Clientes/Documentos/TiposDocumento a la vez para pintar la
/// lista), y algunas menos frecuentes de un Command escribiendo directamente
/// en el repositorio de otra feature (p. ej. <c>Importacion</c>, que orquesta
/// la carga combinada de varios agregados en una sola transacción). Eliminar
/// ese acoplamiento es un refactor mucho mayor (Horizonte 2.2, retirada de
/// <c>PuertaAccesoDatos</c>) y está fuera de alcance aquí.
///
/// Esto es el ratchet: se congela el inventario real de hoy —obtenido con la
/// misma reflexión que hace este test, no a mano— como lista blanca explícita.
/// Cualquier referencia cruzada NUEVA que no esté ya en la lista hace fallar
/// el test con su nombre exacto, obligando a que la incorporación sea una
/// decisión deliberada (añadir la tupla aquí, en el mismo commit que la
/// introduce) en vez de un acoplamiento que se cuela sin que nadie lo note.
/// No se falla por entradas de la lista que ya no se usen (el objetivo es
/// impedir la regresión, no perseguir la limpieza de la lista).
/// </summary>
public class FronterasEntrePersistenciaDeFeaturesTests
{
    /// <summary>
    /// Los nombres con los que esta base llama a una interfaz de persistencia.
    ///
    /// <para>
    /// <c>Writer</c> se añadió tras comprobar que su ausencia no era teórica:
    /// <c>IAsignacionesOperativasWriter</c> es la persistencia de la feature
    /// <c>Operaciones</c> y <b>ocho</b> handlers de <c>Clientes</c> y
    /// <c>Tenants</c> ya dependían de ella. Eran referencias cruzadas reales,
    /// sin entrada en la lista blanca y sin que el ratchet las viera, solo
    /// porque el nombre no terminaba en <c>Repository</c> ni en
    /// <c>QueryContext</c>.
    /// </para>
    /// </summary>
    private static readonly Regex NombreInterfazDePersistencia =
        new(@"^I.*(Repository|QueryContext|Writer)$", RegexOptions.Compiled);

    // Congelado desde el inventario real a 2026-08-14 (Horizonte 2.5). Cada
    // tupla es (feature del handler + nombre del handler, nombre de la
    // interfaz de persistencia ajena de la que depende).
    private static readonly HashSet<(string Handler, string Interfaz)> ReferenciasCruzadasPermitidas = new()
    {
        ("Alertas.ObtenerAlertasQueryHandler", "IAsignacionesQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ICentrosQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IClientesQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IConfiguracionQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IDocumentosQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IEmpresasQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ITiposDocumentoQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ITrabajadoresQueryContext"),
        ("Alertas.ObtenerSugerenciasPreventivasQueryHandler", "IAsignacionesQueryContext"),
        ("Alertas.ObtenerSugerenciasPreventivasQueryHandler", "ICentrosQueryContext"),
        ("Alertas.ObtenerSugerenciasPreventivasQueryHandler", "ITrabajadoresQueryContext"),
        ("ApiKeys.GenerarClaveApiCommandHandler", "IDelegacionTenantRepository"),
        ("ApiKeys.GenerarClaveApiCommandHandler", "ITenantsQueryContext"),
        ("ApiKeys.ObtenerClavesApiQueryHandler", "ITenantsQueryContext"),
        ("ApiKeys.RevocarClaveApiCommandHandler", "IDelegacionTenantRepository"),
        ("ApiKeys.RevocarClaveApiCommandHandler", "ITenantsQueryContext"),
        ("Asignaciones.CrearAsignacionCommandHandler", "ICentrosQueryContext"),
        ("Asignaciones.CrearAsignacionCommandHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.CrearAsignacionesCommandHandler", "ICentrosQueryContext"),
        ("Asignaciones.CrearAsignacionesCommandHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.ObtenerAsignacionesDocumentacionPorCentroQueryHandler", "IConfiguracionQueryContext"),
        ("Asignaciones.ObtenerAsignacionesDocumentacionPorCentroQueryHandler", "IDocumentosQueryContext"),
        ("Asignaciones.ObtenerAsignacionesDocumentacionPorCentroQueryHandler", "ITiposDocumentoQueryContext"),
        ("Asignaciones.ObtenerAsignacionesDocumentacionPorCentroQueryHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.ObtenerAsignacionesQueryHandler", "ICentrosQueryContext"),
        ("Asignaciones.ObtenerAsignacionesQueryHandler", "IClientesQueryContext"),
        ("Asignaciones.ObtenerAsignacionesQueryHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.ObtenerDocumentosFaltantesParaAsignacionQueryHandler", "ICentrosQueryContext"),
        ("Asignaciones.ObtenerDocumentosFaltantesParaAsignacionQueryHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.ObtenerTrabajadoresVisitaSinAsignacionQueryHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.ObtenerTrabajadoresVisitaSinAsignacionQueryHandler", "IVisitasQueryContext"),
        ("Bandeja.ObtenerBandejaGestorQueryHandler", "IConfiguracionQueryContext"),
        ("Blindaje42.ObtenerBlindajeEmpresasDeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Blindaje42.SolicitarCertificacionTgssCommandHandler", "IEmpresasQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ICentrosQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IClientesQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IDocumentosQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IEmpresasQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IProyectosQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ISubcontratasQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ITiposDocumentoQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ITrabajadoresQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IVehiculosQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "IConfiguracionQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "IDocumentosQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "ITiposDocumentoQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.CrearCanalGestionCommandHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Centros.CrearCentroCommandHandler", "IClientesQueryContext"),
        ("Centros.CrearCentroCommandHandler", "IEmpresasQueryContext"),
        ("Centros.CrearCentroCommandHandler", "ITipoDocumentoCentroRepository"),
        ("Centros.CrearCentroCommandHandler", "ITiposDocumentoQueryContext"),
        ("Centros.EditarCanalGestionCommandHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Centros.EliminarCentroCommandHandler", "IAsignacionRepository"),
        ("Centros.EliminarCentrosCommandHandler", "IAsignacionRepository"),
        ("Centros.EliminarDocumentacionRequeridaCentroCommandHandler", "ITipoDocumentoCentroRepository"),
        ("Centros.EstablecerDocumentacionRequeridaCentroCommandHandler", "ITipoDocumentoCentroRepository"),
        ("Centros.EstablecerDocumentacionRequeridaCentroCommandHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerCanalesGestionDeCentroQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Centros.ObtenerCentroPorIdQueryHandler", "IClientesQueryContext"),
        ("Centros.ObtenerCentroPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerCentrosParaSelectorQueryHandler", "IClientesQueryContext"),
        ("Centros.ObtenerCentrosParaSelectorQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerCentrosQueryHandler", "IClientesQueryContext"),
        ("Centros.ObtenerCentrosQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IClientesQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IDocumentosQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.ObtenerDocumentacionRequeridaDeCentroQueryHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "ISubcontratasQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.ObtenerVehiculosConActividadDeCentroQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerVehiculosConActividadDeCentroQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerVehiculosConActividadDeCentroQueryHandler", "ISubcontratasQueryContext"),
        ("Centros.ObtenerVehiculosConActividadDeCentroQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.ObtenerVehiculosConActividadDeCentroQueryHandler", "IVehiculosQueryContext"),
        // ── F3b: redirección de escritura de Cliente a Empresa ─────────────
        //
        // Desde la congelación (D2), "Cliente" es una Empresa contraparte
        // (Empresa.CrearComoCliente): los comandos de la feature Clientes
        // escriben/leen ahora directamente contra el repositorio y el
        // contexto de consulta de Empresas en vez de contra los suyos
        // propios, que se retiraron. Es cruce de feature deliberado, no un
        // acoplamiento colado — Cliente y Empresa comparten agregado físico
        // hasta F4.
        ("Clientes.CrearClienteCommandHandler", "IEmpresaRepository"),
        ("Clientes.EditarClienteCommandHandler", "IEmpresaRepository"),
        ("Clientes.EliminarClienteCommandHandler", "IEmpresaRepository"),
        ("Clientes.EliminarClientesCommandHandler", "IEmpresaRepository"),
        ("Clientes.ReasignarEjecutivoClienteCommandHandler", "IEmpresaRepository"),
        ("Clientes.RestaurarClienteCommandHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerCentrosDeClienteQueryHandler", "ICentrosQueryContext"),
        ("Clientes.ObtenerCentrosDeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerClientesQueryHandler", "ICentrosQueryContext"),
        ("Clientes.ObtenerClientesQueryHandler", "IContactosAgendaQueryContext"),
        ("Clientes.ObtenerEmpresasDeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerResumenClienteQueryHandler", "IAsignacionesQueryContext"),
        ("Clientes.ObtenerResumenClienteQueryHandler", "ICentrosQueryContext"),
        // F4.2b: repuntado a RelacionesEmpresariales — ya no depende de
        // ISubcontratasQueryContext en absoluto.
        ("Clientes.ObtenerSubcontratasDeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ReasignarEjecutivoClienteCommandHandler", "IConfiguracionIaDocumentoClienteRepository"),
        ("Clientes.ReasignarEjecutivoClienteCommandHandler", "INotificacionUsuarioRepository"),
        // F3b-Subcontrata: mismo motivo que Cliente arriba — "Subcontrata" es
        // ahora una Empresa contraparte (Empresa.CrearComoSubcontrata), y los
        // comandos de la feature Subcontratas escriben/leen contra el
        // repositorio de Empresas en vez del suyo propio, retirado.
        ("Subcontratas.CrearSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.EditarSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.EliminarSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.EliminarSubcontratasCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.CambiarNivelServicioSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.GuardarCredencialAccesoSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "IEmpresaRepository"),
        // F3b-Subcontrata: el detalle de Subcontrata (RazonSocial/Cif/NivelServicio)
        // ahora vive en Empresas; ISubcontratasQueryContext se conserva en este
        // handler solo por SubcontratasClientes/SubcontratasEmpresas.
        ("Subcontratas.ObtenerSubcontrataPorIdQueryHandler", "IEmpresasQueryContext"),
        // F3b-Subcontrata (revisión adversaria, 2026-08-26): adelantadas por
        // evidencia real, no por reclasificación — ver
        // f3b-subcontrata-obtenersubcontratasquery-adelantada-2026-08-26.md
        // y f3b-subcontrata-selector-adelantado-2026-08-26.md.
        ("Subcontratas.ObtenerSubcontratasQueryHandler", "IEmpresasQueryContext"),
        ("Subcontratas.ObtenerSubcontratasParaSelectorQueryHandler", "IEmpresasQueryContext"),
        // Comercial (Horizonte 1.7, "Billing mínimo viable") opera
        // directamente sobre el agregado Tenant (EstadoComercial,
        // StripeCustomerId/SubscriptionId) — mismo motivo que ApiKeys arriba:
        // no tiene sentido una feature de "billing" con su propio
        // repositorio de Tenant en paralelo al de Tenants.
        ("Comercial.ActualizarEstadoComercialTenantCommandHandler", "ITenantsQueryContext"),
        ("Comercial.ObtenerEstadoComercialTenantsQueryHandler", "ITenantsQueryContext"),
        ("Comercial.RegistrarSuscripcionTenantCommandHandler", "ITenantsQueryContext"),
        ("Comunicaciones.ActualizarDocumentoDesdeAdjuntoCommandHandler", "IDocumentosQueryContext"),
        ("Comunicaciones.AsignarClienteConversacionCommandHandler", "IEmpresaRepository"),
        ("Comunicaciones.CrearMacroCommandHandler", "IEmpresaRepository"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "ITrabajadoresQueryContext"),
        ("Comunicaciones.EditarMacroCommandHandler", "IEmpresaRepository"),
        ("Comunicaciones.EnviarMensajeNuevoCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.MigrarConversacionACorreoCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.MigrarConversacionACorreoCommandHandler", "IIntegracionesQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "IAsignacionesQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "ICentrosQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "IIntegracionesQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "ISubcontratasQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.ObtenerBorradorPedirPrioridadQueryHandler", "ITrabajadoresQueryContext"),
        ("Comunicaciones.ObtenerConversacionesQueryHandler", "IClientesQueryContext"),
        ("Comunicaciones.ObtenerConversacionesQueryHandler", "IIntegracionesQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "ICentrosQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "IClientesQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "IDocumentosQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "IReclamacionesQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "ITrabajadoresQueryContext"),
        ("Comunicaciones.ObtenerConversacionPorIdQueryHandler", "IVisitasQueryContext"),
        ("Comunicaciones.ObtenerFormatosRequeridosCentroQueryHandler", "ICentrosQueryContext"),
        ("Comunicaciones.ObtenerFormatosRequeridosCentroQueryHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.ObtenerMacrosQueryHandler", "IClientesQueryContext"),
        ("Comunicaciones.ObtenerMensajesBuzonPersonalQueryHandler", "IIntegracionesQueryContext"),
        ("Comunicaciones.ObtenerMensajesBuzonPersonalQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Comunicaciones.ObtenerSugerenciasVisitaCorreoPendientesQueryHandler", "ICentrosQueryContext"),
        ("Comunicaciones.ObtenerSugerenciasVisitaCorreoPendientesQueryHandler", "IClientesQueryContext"),
        ("Comunicaciones.PedirPrioridadValidacionCommandHandler", "ICentrosQueryContext"),
        ("Comunicaciones.PedirPrioridadValidacionCommandHandler", "IIntegracionesQueryContext"),
        ("Comunicaciones.ResponderConversacionCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.ResponderConversacionWhatsAppCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.ResponderConversacionWhatsAppCommandHandler", "ILineaWhatsAppRepository"),
        ("Contactos.GuardarContactoAgendaCommandHandler", "ITiposDocumentoQueryContext"),
        ("Contactos.ObtenerAgendaContactosQueryHandler", "ITiposDocumentoQueryContext"),
        // REC-035 (HO-035-02): el Administrador de plataforma registra la
        // instrucción sobre un Tenant propietario elegido de la lista
        // completa de tenants — mismo criterio que ApiKeys.GenerarClaveApiCommandHandler
        // arriba, que también depende de ITenantsQueryContext para resolver
        // el tenant objetivo fuera de su propia feature.
        ("Cumplimiento.RegistrarInstruccionTratamientoIaTenantPropietarioCommandHandler", "ITenantsQueryContext"),
        ("Dashboard.GuardarPreferenciaDashboardCommandHandler", "IPreferenciaDashboardUsuarioRepository"),
        // Horizonte 2.7: CalcularFacturacionEstimadaAsync pasó de despachar
        // ObtenerResumenFacturacionQuery por Mediator (la dependencia cruzada
        // quedaba oculta dentro de ese handler) a inyectar directamente los
        // QueryContext que necesita para calcular los 7 ConceptoFacturable
        // agrupados por cliente en una sola consulta — el fix real del N+1,
        // no un acoplamiento nuevo: la composición cruzada ya existía, solo
        // se hizo visible en el constructor en vez de estar un salto de
        // Mediator más lejos.
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IAsignacionesQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IConfiguracionQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IDocumentosIaQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IFacturacionQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IIncidenciasQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IProyectosQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "ITrabajadoresQueryContext"),
        ("Dashboard.ObtenerCatalogoKpisQueryHandler", "IVisitasQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "IAsignacionesQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "IClientesQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "IConfiguracionQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "IEmpresasQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "ISubcontratasQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "ITiposDocumentoQueryContext"),
        ("Dashboard.ObtenerDesgloseDashboardQueryHandler", "ITrabajadoresQueryContext"),
        ("Dashboard.ObtenerEstadisticasAprobacionDocumentoQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IClientesQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IComunicacionesQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IConfiguracionQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "ITelemetriaQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IVisitasQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IConfiguracionQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "ITrabajadoresQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IVisitasQueryContext"),
        ("Dashboard.ObtenerPendientePorPlataformaQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerPendientePorPlataformaQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerPendientePorPlataformaQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Dashboard.ObtenerPreferenciaDashboardQueryHandler", "IPreferenciaDashboardUsuarioRepository"),
        ("Dashboard.ObtenerPulsoEquipoQueryHandler", "IDocumentosQueryContext"),
        ("Documentos.AplicarDeteccionIaDocumentoCommandHandler", "IAuditoriaExtraccionIaRepository"),
        ("Documentos.AplicarDeteccionIaDocumentoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.AplicarDeteccionIaDocumentoCommandHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.CrearDocumentoCommandHandler", "IClientesQueryContext"),
        ("Documentos.CrearDocumentoCommandHandler", "IEmpresasQueryContext"),
        ("Documentos.CrearDocumentoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.CrearDocumentoCommandHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.CrearDocumentoCommandHandler", "ITrabajadoresQueryContext"),
        ("Documentos.CrearDocumentoCommandHandler", "ITrabajoAnalisisDocumentoRepository"),
        ("Documentos.CrearDocumentoCommandHandler", "IVehiculosQueryContext"),
        ("Documentos.DetectarCamposDocumentoQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.DetectarCamposDocumentoQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.EliminarDocumentoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.EliminarDocumentosCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionAceptadaCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionRechazadaCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionSubidaCommandHandler", "IProyectosQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "ICentrosQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "IClientesQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IClientesQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IProyectosQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IVehiculosQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "ICentrosQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "IClientesQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "IConfiguracionQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "IProyectosQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.ObtenerDocumentosQueryHandler", "IVehiculosQueryContext"),
        ("Documentos.ObtenerRevisionesIaPendientesQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerRevisionesIaPendientesQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerRevisionesIaPendientesQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.RenovarDocumentoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.RenovarDocumentoCommandHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.RenovarDocumentoCommandHandler", "ITrabajoAnalisisDocumentoRepository"),
        // Mismo par que RenovarDocumentoCommandHandler: DocumentoAlcanceExtensions.DocumentoVisibleAsync
        // necesita IProyectosQueryContext para resolver el ClienteId de un Documento de Proyecto,
        // y la guarda de perfil oficial (Fase A de firma en campo) necesita ITiposDocumentoQueryContext.
        ("Documentos.FirmarDocumentoEnCampoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.FirmarDocumentoEnCampoCommandHandler", "ITiposDocumentoQueryContext"),
        // El sello guardado (Fase A de firma en campo) cuelga de la Empresa —
        // necesita confirmar que existe antes de guardarle un sello.
        ("Documentos.GuardarSelloEmpresaCommandHandler", "IEmpresaRepository"),
        // El selector de "incluir sello" en la pestaña Firma necesita el
        // nombre de las Empresas del tenant que tienen sello guardado.
        ("Documentos.ObtenerEmpresasConSelloGuardadoQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ResolverRevisionIaDocumentoCommandHandler", "IAuditoriaExtraccionIaRepository"),
        ("Empresas.CrearEmpresaCommandHandler", "IClientesQueryContext"),
        ("Empresas.EditarEmpresaCommandHandler", "IClientesQueryContext"),
        // F4.2c — RelacionEmpresarial es la única fuente de escritura de los
        // vínculos empresariales (R6 aceptada 2026-08-27): estos cruces de
        // feature son el destino final del diseño, no una transición.
        ("Empresas.CrearEmpresaCommandHandler", "IRelacionEmpresarialRepository"),
        ("Empresas.EditarEmpresaCommandHandler", "IRelacionEmpresarialRepository"),
        ("Empresas.ObtenerCentrosConActividadDeEmpresaQueryHandler", "IAsignacionesQueryContext"),
        ("Empresas.ObtenerCentrosConActividadDeEmpresaQueryHandler", "ICentrosQueryContext"),
        ("Empresas.ObtenerCentrosConActividadDeEmpresaQueryHandler", "IClientesQueryContext"),
        ("Empresas.ObtenerCentrosConActividadDeEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Empresas.ObtenerClientesDeEmpresaQueryHandler", "IClientesQueryContext"),
        ("Empresas.ObtenerCumplimientoEmpresaQueryHandler", "IAsignacionesQueryContext"),
        ("Empresas.ObtenerCumplimientoEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Empresas.ObtenerEmpresasQueryHandler", "IAsignacionesQueryContext"),
        ("Empresas.ObtenerEmpresasQueryHandler", "ITrabajadoresQueryContext"),
        ("Facturacion.CrearTarifaClienteCommandHandler", "IClientesQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "IAsignacionesQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "ICentrosQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "IClientesQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "IDocumentosQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "IProyectosQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "ITrabajadoresQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "IVisitasQueryContext"),
        ("Facturacion.ObtenerTarifasClienteQueryHandler", "IClientesQueryContext"),
        ("Gestiones.CrearGestionesParaTrabajadorCommandHandler", "IAsignacionesQueryContext"),
        ("Gestiones.CrearGestionesParaTrabajadorCommandHandler", "IDetalleSugerenciaGestionCorreoRepository"),
        ("Gestiones.CrearGestionesParaTrabajadorCommandHandler", "ITipoDocumentoRepository"),
        ("Gestiones.ObtenerGestionesQueryHandler", "ICentrosQueryContext"),
        ("Gestiones.ObtenerGestionesQueryHandler", "ITiposDocumentoQueryContext"),
        ("Gestiones.ObtenerGestionesQueryHandler", "ITrabajadoresQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "ICentroRepository"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "ICentrosQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IEmpresaRepository"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IEmpresasQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "ITrabajadoresQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "ITrabajadorRepository"),
        // F4.2c — mismo destino final que el bloque de Empresas de arriba.
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IRelacionEmpresarialRepository"),
        ("Importacion.EjecutarImportacionCommandHandler", "IAsignacionesQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "IAsignacionRepository"),
        ("Importacion.EjecutarImportacionCommandHandler", "ICentrosQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "IClientesQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "IDocumentoRepository"),
        ("Importacion.EjecutarImportacionCommandHandler", "IDocumentosQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "IEmpresaRepository"),
        ("Importacion.EjecutarImportacionCommandHandler", "IEmpresasQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "ITiposDocumentoQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "ITrabajadoresQueryContext"),
        ("Importacion.EjecutarImportacionCommandHandler", "ITrabajadorRepository"),
        ("Incidencias.CrearIncidenciaCommandHandler", "ICentrosQueryContext"),
        ("Incidencias.CrearIncidenciaCommandHandler", "ITrabajadoresQueryContext"),
        ("Incidencias.EditarIncidenciaCommandHandler", "ITrabajadoresQueryContext"),
        ("Incidencias.ObtenerIncidenciaPorIdQueryHandler", "ICentrosQueryContext"),
        ("Incidencias.ObtenerIncidenciasQueryHandler", "ICentrosQueryContext"),
        ("Incidencias.ObtenerIncidenciasQueryHandler", "ITrabajadoresQueryContext"),
        ("Integraciones.ConectarBuzonMicrosoft365CommandHandler", "IEmpresaRepository"),
        ("Integraciones.CrearLineaWhatsAppCommandHandler", "IEmpresaRepository"),
        ("Integraciones.ObtenerConexionesIntegracionQueryHandler", "IClientesQueryContext"),
        ("Integraciones.ObtenerLineasWhatsAppQueryHandler", "IClientesQueryContext"),
        // Alta de plantilla exige un TipoDocumento existente cuyo Ambito coincida
        // (mismo criterio que Documentos.CrearDocumentoCommandHandler).
        ("Plantillas.CrearPlantillaDocumentoCommandHandler", "ITiposDocumentoQueryContext"),
        // Generación individual: resuelve TipoDocumento/Empresa/Trabajador/Centro/
        // Cliente/ContactoAgenda para rellenar la plantilla y crea el Documento real
        // (Domain.Documentos.IDocumentoRepository, otra feature de Domain).
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IAsignacionRepository"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "ICentrosQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IClientesQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IContactosAgendaQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IDocumentoRepository"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IEmpresasQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "ITiposDocumentoQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "ITrabajadoresQueryContext"),
        // Progreso de lote: nombre del trabajador de cada item, para no obligar a la UI a resolverlo aparte.
        ("Plantillas.ObtenerLoteGeneracionDocumentosQueryHandler", "ITrabajadoresQueryContext"),
        // Trazabilidad: nombre del trabajador/empresa de cada DocumentoGenerado.
        ("Plantillas.ObtenerDocumentosGeneradosQueryHandler", "ITrabajadoresQueryContext"),
        ("Plantillas.ObtenerDocumentosGeneradosQueryHandler", "IEmpresasQueryContext"),
        ("Proyectos.AsignarTecnicoProyectoCommandHandler", "ITrabajadoresQueryContext"),
        ("Proyectos.CrearProyectoCommandHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "IClientesQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "IDocumentosQueryContext"),
        ("Proyectos.ObtenerProyectosParaSelectorQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerProyectosParaSelectorQueryHandler", "IClientesQueryContext"),
        ("Proyectos.ObtenerProyectosQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerTecnicosProyectoQueryHandler", "ITrabajadoresQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ICentrosQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IClientesQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ITrabajadoresQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaCommandHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaCommandHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionEmpresaQueryHandler", "IConfiguracionQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionEmpresaQueryHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionEmpresaQueryHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "ICentrosQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IClientesQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IConfiguracionQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "ITrabajadoresQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesEnviadasQueryHandler", "IClientesQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesEnviadasQueryHandler", "IComunicacionesQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesSinRespuestaQueryHandler", "IClientesQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesSinRespuestaQueryHandler", "IComunicacionesQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "IAsignacionesQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "ICentrosQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "IClientesQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "IConfiguracionQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "IDocumentosQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "IEmpresasQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "ISubcontratasQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "ITiposDocumentoQueryContext"),
        ("Reportes.GenerarInformeVigenciaQueryHandler", "ITrabajadoresQueryContext"),
        ("Reportes.GenerarInformeAsignacionesQueryHandler", "IAsignacionesQueryContext"),
        ("Reportes.GenerarInformeAsignacionesQueryHandler", "ICentrosQueryContext"),
        ("Reportes.GenerarInformeAsignacionesQueryHandler", "IClientesQueryContext"),
        ("Reportes.GenerarInformeAsignacionesQueryHandler", "ITrabajadoresQueryContext"),
        ("Subcontratas.CrearSubcontrataCommandHandler", "IClientesQueryContext"),
        ("Subcontratas.CrearSubcontrataCommandHandler", "IEmpresasQueryContext"),
        ("Subcontratas.EditarSubcontrataCommandHandler", "IClientesQueryContext"),
        ("Subcontratas.EditarSubcontrataCommandHandler", "IEmpresasQueryContext"),
        // F4 — misma doble escritura transitoria de arriba.
        ("Subcontratas.CrearSubcontrataCommandHandler", "IRelacionEmpresarialRepository"),
        ("Subcontratas.EditarSubcontrataCommandHandler", "IRelacionEmpresarialRepository"),
        ("Subcontratas.ObtenerCentrosConActividadDeSubcontrataQueryHandler", "IAsignacionesQueryContext"),
        ("Subcontratas.ObtenerCentrosConActividadDeSubcontrataQueryHandler", "ICentrosQueryContext"),
        ("Subcontratas.ObtenerCentrosConActividadDeSubcontrataQueryHandler", "IClientesQueryContext"),
        ("Subcontratas.ObtenerCentrosConActividadDeSubcontrataQueryHandler", "ITrabajadoresQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "IAsignacionesQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "ICentrosQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "IClientesQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "IConfiguracionQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "ITiposDocumentoQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "ITrabajadoresQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IAsignacionesQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IConfiguracionQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IDocumentosQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "ITiposDocumentoQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "ITrabajadoresQueryContext"),
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "ICentrosQueryContext"),
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "ITiposDocumentoQueryContext"),
        ("Telemetria.ObtenerTiempoGestionConversacionQueryHandler", "IConfiguracionQueryContext"),
        ("Telemetria.RegistrarTramoGestionCommandHandler", "IComunicacionesQueryContext"),
        ("Telemetria.RegistrarTramoGestionCommandHandler", "IConfiguracionQueryContext"),
        ("Tenants.AbrirAccesoSoporteCommandHandler", "IRegistroActividadSoporteRepository"),
        ("Tenants.CerrarAccesoSoporteCommandHandler", "IRegistroActividadSoporteRepository"),
        ("Tenants.CrearClienteDeleganteCommandHandler", "IParametroSistemaRepository"),
        ("TiposDocumento.ActualizarDeteccionTrabajadoresGlobalCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.ActualizarLecturaIaClienteCommandHandler", "IClientesQueryContext"),
        ("TiposDocumento.ActualizarLecturaIaClienteCommandHandler", "IConfiguracionIaDocumentoClienteRepository"),
        ("TiposDocumento.ActualizarLecturaIaGlobalCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.ActualizarPerfilDocumentoOficialGlobalCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.ActualizarVerificacionIaGlobalCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.CrearTipoDocumentoCommandHandler", "ICentrosQueryContext"),
        ("TiposDocumento.CrearTipoDocumentoCommandHandler", "ITipoDocumentoCentroRepository"),
        ("TiposDocumento.CrearTipoDocumentoCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.EditarTipoDocumentoCommandHandler", "ICentrosQueryContext"),
        ("TiposDocumento.EditarTipoDocumentoCommandHandler", "ITipoDocumentoCentroRepository"),
        ("TiposDocumento.EditarTipoDocumentoCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.ObtenerTiposDocumentoQueryHandler", "ICentrosQueryContext"),
        ("Trabajadores.CrearTrabajadorCommandHandler", "IEmpresasQueryContext"),
        ("Trabajadores.CrearTrabajadorCommandHandler", "ISubcontratasQueryContext"),
        ("Trabajadores.EliminarTrabajadorCommandHandler", "IAsignacionRepository"),
        ("Trabajadores.EliminarTrabajadoresCommandHandler", "IAsignacionRepository"),
        ("Trabajadores.ObtenerDeteccionesPendientesQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IAsignacionesQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "ICentrosQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IClientesQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IConfiguracionQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IDocumentosQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "ITiposDocumentoQueryContext"),
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "ISubcontratasQueryContext"),
        ("Trabajadores.ObtenerTrabajadorPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerTrabajadorPorIdQueryHandler", "ISubcontratasQueryContext"),
        ("Vehiculos.CrearVehiculoCommandHandler", "IEmpresasQueryContext"),
        ("Vehiculos.CrearVehiculoCommandHandler", "ISubcontratasQueryContext"),
        ("Vehiculos.ObtenerVehiculoPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Vehiculos.ObtenerVehiculoPorIdQueryHandler", "ISubcontratasQueryContext"),
        ("Vehiculos.ObtenerVehiculosQueryHandler", "IEmpresasQueryContext"),
        ("Vehiculos.ObtenerVehiculosQueryHandler", "ISubcontratasQueryContext"),
        ("Visitas.CrearVisitaCommandHandler", "ICentrosQueryContext"),
        ("Visitas.CrearVisitaCommandHandler", "IComunicacionesQueryContext"),
        ("Visitas.CrearVisitaCommandHandler", "ISugerenciaVisitaCorreoRepository"),
        ("Visitas.CrearVisitaCommandHandler", "ITrabajadoresQueryContext"),
        ("Visitas.EditarVisitaCommandHandler", "ITrabajadoresQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "IClientesQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "ITrabajadoresQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "IConfiguracionQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "IDocumentosQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "ISubcontratasQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "ITiposDocumentoQueryContext"),
        ("Visitas.ObtenerDocumentacionVisitaQueryHandler", "ITrabajadoresQueryContext"),
        ("Visitas.ObtenerVisitaPorIdQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerVisitaPorIdQueryHandler", "IClientesQueryContext"),
        ("Visitas.ObtenerVisitaPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerVisitasParaCalendarioQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerVisitasParaCalendarioQueryHandler", "IClientesQueryContext"),
        ("Visitas.ObtenerVisitasQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerVisitasQueryHandler", "IClientesQueryContext"),
        ("Visitas.ObtenerVisitasQueryHandler", "IConfiguracionQueryContext"),
        ("Visitas.ObtenerVisitasQueryHandler", "IDocumentosQueryContext"),
        ("Visitas.ObtenerVisitasQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerVisitasQueryHandler", "ITiposDocumentoQueryContext"),

        // ── Doble escritura de F1 ──────────────────────────────────────────
        //
        // Aparecieron al ampliar el patrón de nombres a los *Writer, y son
        // legítimas: mientras dure la transición, todo comando que cambie el
        // reparto por el modelo antiguo (DelegacionTenant, Cliente.Ejecutivo…)
        // tiene que escribir también las tablas de asignación, en la misma
        // transacción. Esa es la razón de que la feature Operaciones exponga un
        // escritor y no un repositorio: es una operación de mantenimiento de la
        // proyección, no la persistencia de un agregado ajeno.
        //
        // Se retiran cuando se retire la proyección EjecutivoUsuarioId. Hasta
        // entonces, ocho entradas explícitas valen más que un patrón que no las
        // veía: durante meses fueron referencias cruzadas reales, sin lista y
        // sin ratchet, porque el nombre no acababa en Repository ni QueryContext.
        ("Clientes.CrearClienteCommandHandler", "IAsignacionesOperativasWriter"),
        ("Clientes.ReasignarEjecutivoClienteCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearAsignacionOperadorDelegadoCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearClienteDeleganteCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearDelegacionTenantCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.DesactivarDelegacionTenantCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.ReactivarDelegacionTenantCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.RevocarAsignacionOperadorDelegadoCommandHandler", "IAsignacionesOperativasWriter"),

        // ── F3b: lectores de categoría B redirigidos de Clientes a Empresas ─
        //
        // Cada uno de estos handlers leía un ClienteId ya conocido (de un
        // Centro/Documento/Proyecto/Reclamación/Conversación/etc. ya cargado)
        // contra IClientesQueryContext — la tabla legacy Clientes. Desde la
        // congelación de Cliente (D2), esos Ids solo existen en Empresas para
        // cualquier fila creada después del corte, así que la lectura tenía
        // que repuntar también. Cruce de feature deliberado, mismo motivo que
        // el bloque de escritura de más arriba: Cliente y Empresa comparten
        // agregado físico hasta F4.
        ("Asignaciones.ObtenerAsignacionesQueryHandler", "IEmpresasQueryContext"),

        // F4-P0 (2026-08-27): la última de las 6 consultas semánticas que D2
        // había dejado congeladas — la propia lista de /clientes. Sin
        // escrituras a la tabla legacy Clientes desde F3b, cualquier Cliente
        // dado de alta después del freeze era invisible en su propio
        // listado: problema de producto visible, no incertidumbre
        // arquitectónica, así que se repunta ahora sin esperar a F4.2b.
        // Discrimina Cliente de Empresa propia/Subcontrata con
        // EsCritico != null (mismo patrón que ObtenerSubcontratasQueryHandler
        // usa NivelServicio != null).
        ("Clientes.ObtenerClientesQueryHandler", "IEmpresasQueryContext"),

        // Este NO es un lector de categoría B (Id ya conocido) — es una de
        // las 6 consultas semánticas que D2 dejó congeladas, adelantada
        // deliberadamente antes de F4 tras una revisión adversaria (E2E real
        // en rojo: el asistente de alta guiada crea un Cliente y lo vincula
        // en la misma sesión vía este selector; congelado, el selector nunca
        // lo encontraba). Las otras 5 consultas de D2 §3 siguen intactas —
        // ver f3b-selectores-adelantados-2026-08-26.md.
        ("Clientes.ObtenerClientesParaSelectorQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerClientePorIdQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerResumenClienteQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.ObtenerConversacionesQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.ObtenerMacrosQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.ObtenerSugerenciasVisitaCorreoPendientesQueryHandler", "IEmpresasQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IEmpresasQueryContext"),
        ("Facturacion.CrearTarifaClienteCommandHandler", "IEmpresasQueryContext"),
        ("Facturacion.ObtenerResumenFacturacionQueryHandler", "IEmpresasQueryContext"),
        ("Facturacion.ObtenerTarifasClienteQueryHandler", "IEmpresasQueryContext"),
        ("Integraciones.ObtenerConexionesIntegracionQueryHandler", "IEmpresasQueryContext"),
        ("Integraciones.ObtenerLineasWhatsAppQueryHandler", "IEmpresasQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Proyectos.ObtenerProyectosParaSelectorQueryHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaCommandHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionEmpresaQueryHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesEnviadasQueryHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesSinRespuestaQueryHandler", "IEmpresasQueryContext"),
        ("Reportes.GenerarInformeAsignacionesQueryHandler", "IEmpresasQueryContext"),
        ("Subcontratas.ObtenerCentrosConActividadDeSubcontrataQueryHandler", "IEmpresasQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "IEmpresasQueryContext"),
        ("TiposDocumento.ActualizarLecturaIaClienteCommandHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ResolverDeteccionAusenteCommandHandler", "IAsignacionRepository"),
        ("Visitas.ObtenerVisitasParaCalendarioQueryHandler", "IEmpresasQueryContext"),
    };

    [Fact]
    public void Ninguna_referencia_cruzada_de_persistencia_entre_features_nueva_sin_lista_blanca()
    {
        var application = typeof(ICommand).Assembly;

        var handlers = application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.GetInterfaces()
                .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)));

        var infractores = new List<string>();

        foreach (var handler in handlers)
        {
            var featureHandler = Feature(handler.Namespace);
            if (featureHandler is null) continue; // fuera de src/CaeManager.Application/<Feature>/..., no aplica

            var dependenciasDePersistencia = handler.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.ParameterType)
                .Distinct()
                .Where(t => NombreInterfazDePersistencia.IsMatch(t.Name));

            foreach (var dependencia in dependenciasDePersistencia)
            {
                var featureDependencia = Feature(dependencia.Namespace);
                if (featureDependencia is null) continue; // Common/DependencyInjection: no es "otra feature"
                if (featureDependencia == featureHandler) continue; // dependencia dentro de su propia feature

                var referencia = ($"{featureHandler}.{handler.Name}", dependencia.Name);
                if (ReferenciasCruzadasPermitidas.Contains(referencia)) continue;

                infractores.Add($"(\"{referencia.Item1}\", \"{referencia.Item2}\")");
            }
        }

        string.Join(", ", infractores.Distinct().OrderBy(x => x)).Should().BeEmpty(
            "una referencia cruzada nueva entre features tiene que ser una decisión deliberada: si el handler " +
            "listado necesita de verdad la interfaz de la otra feature, añade la tupla a " +
            "ReferenciasCruzadasPermitidas en este mismo commit — si no la necesita, es la señal de que se " +
            "coló un acoplamiento que el revisor no pilló");
    }

    // "Feature" = segundo segmento del namespace bajo CaeManager.Application
    // (lectura, I*QueryContext) o CaeManager.Domain (escritura, I*Repository).
    // Common/DependencyInjection son compartidos por diseño (p. ej. IUnitOfWork),
    // no pertenecen a ninguna feature y quedan fuera de esta comprobación.
    private static string? Feature(string? ns)
    {
        if (ns is null) return null;

        var segmentos = ns.Split('.');
        if (segmentos.Length < 3) return null;
        if (segmentos[0] != "CaeManager") return null;
        if (segmentos[1] != "Application" && segmentos[1] != "Domain") return null;

        var feature = segmentos[2];
        return feature is "Common" or "DependencyInjection" ? null : feature;
    }
}
