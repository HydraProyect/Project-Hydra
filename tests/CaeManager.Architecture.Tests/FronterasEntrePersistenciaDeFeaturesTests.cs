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
    private static readonly Regex NombreInterfazDePersistencia =
        new(@"^I.*(Repository|QueryContext)$", RegexOptions.Compiled);

    // Congelado desde el inventario real a 2026-08-14 (Horizonte 2.5). Cada
    // tupla es (feature del handler + nombre del handler, nombre de la
    // interfaz de persistencia ajena de la que depende).
    private static readonly HashSet<(string Handler, string Interfaz)> ReferenciasCruzadasPermitidas = new()
    {
        ("Alertas.ObtenerAlertasQueryHandler", "IAsignacionesQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ICentrosQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IConfiguracionQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IDocumentosQueryContext"),
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
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ICentrosQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IClientesQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "IEmpresasQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ISubcontratasQueryContext"),
        ("BusquedaGlobal.BuscarGlobalQueryHandler", "ITrabajadoresQueryContext"),
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
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IDocumentosQueryContext"),
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
        ("Clientes.ObtenerCentrosDeClienteQueryHandler", "ICentrosQueryContext"),
        ("Clientes.ObtenerCentrosDeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerClientesQueryHandler", "IContactosAgendaQueryContext"),
        ("Clientes.ObtenerEmpresasDeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerSubcontratasDeClienteQueryHandler", "ISubcontratasQueryContext"),
        ("Clientes.ReasignarEjecutivoClienteCommandHandler", "IConfiguracionIaDocumentoClienteRepository"),
        ("Clientes.ReasignarEjecutivoClienteCommandHandler", "INotificacionUsuarioRepository"),
        ("Comunicaciones.ActualizarDocumentoDesdeAdjuntoCommandHandler", "IDocumentosQueryContext"),
        ("Comunicaciones.AsignarClienteConversacionCommandHandler", "IClienteRepository"),
        ("Comunicaciones.CrearMacroCommandHandler", "IClienteRepository"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "ITrabajadoresQueryContext"),
        ("Comunicaciones.EditarMacroCommandHandler", "IClienteRepository"),
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
        ("Comunicaciones.PedirPrioridadValidacionCommandHandler", "ICentrosQueryContext"),
        ("Comunicaciones.PedirPrioridadValidacionCommandHandler", "IIntegracionesQueryContext"),
        ("Comunicaciones.ResponderConversacionCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.ResponderConversacionWhatsAppCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.ResponderConversacionWhatsAppCommandHandler", "ILineaWhatsAppRepository"),
        ("Contactos.GuardarContactoAgendaCommandHandler", "ITiposDocumentoQueryContext"),
        ("Contactos.ObtenerAgendaContactosQueryHandler", "ITiposDocumentoQueryContext"),
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
        ("Dashboard.ObtenerPreferenciaDashboardQueryHandler", "IPreferenciaDashboardUsuarioRepository"),
        ("Dashboard.ObtenerPulsoEquipoQueryHandler", "IDocumentosQueryContext"),
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
        ("Empresas.CrearEmpresaCommandHandler", "IClientesQueryContext"),
        ("Empresas.EditarEmpresaCommandHandler", "IClientesQueryContext"),
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
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IClienteRepository"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IClientesQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IEmpresaClienteRepository"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IEmpresaRepository"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "IEmpresasQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "ITrabajadoresQueryContext"),
        ("Importacion.EjecutarImportacionCombinadaCommandHandler", "ITrabajadorRepository"),
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
        ("Integraciones.ConectarBuzonMicrosoft365CommandHandler", "IClienteRepository"),
        ("Integraciones.CrearLineaWhatsAppCommandHandler", "IClienteRepository"),
        ("Integraciones.ObtenerConexionesIntegracionQueryHandler", "IClientesQueryContext"),
        ("Integraciones.ObtenerLineasWhatsAppQueryHandler", "IClientesQueryContext"),
        // Alta de plantilla exige un TipoDocumento existente cuyo Ambito coincida
        // (mismo criterio que Documentos.CrearDocumentoCommandHandler).
        ("Plantillas.CrearPlantillaDocumentoCommandHandler", "ITiposDocumentoQueryContext"),
        // Generación individual: resuelve TipoDocumento/Empresa/Trabajador/Centro/
        // Cliente/ContactoAgenda para rellenar la plantilla y crea el Documento real
        // (Domain.Documentos.IDocumentoRepository, otra feature de Domain).
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "ICentrosQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IClientesQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IContactosAgendaQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IDocumentoRepository"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "IEmpresasQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "ITiposDocumentoQueryContext"),
        ("Plantillas.GenerarDocumentoIndividualCommandHandler", "ITrabajadoresQueryContext"),
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
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IIntegracionesQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ITrabajadoresQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "ICentrosQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IClientesQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IConfiguracionQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.ObtenerLoteReclamacionQueryHandler", "ITrabajadoresQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesSinRespuestaQueryHandler", "IClientesQueryContext"),
        ("Reclamaciones.ObtenerReclamacionesSinRespuestaQueryHandler", "IComunicacionesQueryContext"),
        ("Reportes.ObtenerReporteDocumentosQueryHandler", "IConfiguracionQueryContext"),
        ("Reportes.ObtenerReporteDocumentosQueryHandler", "IDocumentosQueryContext"),
        ("Reportes.ObtenerReporteDocumentosQueryHandler", "IEmpresasQueryContext"),
        ("Reportes.ObtenerReporteDocumentosQueryHandler", "ISubcontratasQueryContext"),
        ("Reportes.ObtenerReporteDocumentosQueryHandler", "ITiposDocumentoQueryContext"),
        ("Reportes.ObtenerReporteDocumentosQueryHandler", "ITrabajadoresQueryContext"),
        ("Subcontratas.CrearSubcontrataCommandHandler", "IClientesQueryContext"),
        ("Subcontratas.CrearSubcontrataCommandHandler", "IEmpresasQueryContext"),
        ("Subcontratas.EditarSubcontrataCommandHandler", "IClientesQueryContext"),
        ("Subcontratas.EditarSubcontrataCommandHandler", "IEmpresasQueryContext"),
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
        ("Trabajadores.ObtenerDeteccionesPendientesQueryHandler", "IEmpresasQueryContext"),
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
