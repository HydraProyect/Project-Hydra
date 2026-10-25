using System.Reflection;
using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Horizonte 2.5 (Project-Hydra-Negocio/MACRO_PLAN_2026-08-13.md § 2.5, regla 1): el código está
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
    /// <c>Tenants</c> dependían de ella (hoy, los de <c>Tenants</c>). Eran referencias cruzadas reales,
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
        // La Delegación y el Oficio del Trabajador son el catálogo de su Empresa (2026-10-18): el handler lee ese
        // catálogo para comprobar que lo elegido es de la Empresa del Trabajador.
        ("Trabajadores.FijarDelegacionEmpresaYOficioTrabajadorCommandHandler", "ICatalogoTrabajadoresEmpresaRepository"),
        ("Trabajadores.CrearTrabajadorCommandHandler", "ICatalogoTrabajadoresEmpresaRepository"),
        ("Trabajadores.EditarFichaTrabajadorCommandHandler", "ICatalogoTrabajadoresEmpresaRepository"),
        // Al restaurar, quita la Delegación o el Oficio que se eliminaron mientras el Trabajador estaba eliminado.
        ("Trabajadores.RestaurarTrabajadorCommandHandler", "ICatalogoTrabajadoresEmpresaRepository"),
        // La lista de Tenants beneficiarios autorizados incluye la vía de
        // Operación (lote 0 del selector de Tenant beneficiario): lee los
        // catálogos de asignación a través del predicado único
        // TenantsBeneficiariosAutorizados, acotado al Tenant de origen.
        ("Tenants.ObtenerClientesAutorizadosQueryHandler", "IOperacionesQueryContext"),
        // El endpoint del logo del Tenant (lote 1) autoriza con ese mismo predicado
        // antes de leer la fila del Tenant pedido (invariante I2 e I11).
        ("Tenants.ObtenerLogoTenantQueryHandler", "IOperacionesQueryContext"),
        // /delegaciones rotula el rol que se ejerce (hallazgo 16, 2026-10-21) con el mismo
        // predicado de operación del Tenant entero que da el acceso (TenantsBeneficiariosAutorizados).
        ("Tenants.ObtenerDelegacionesQueryHandler", "IOperacionesQueryContext"),
        // Ficha de Vehículo: valida la selección de Trabajadores del mismo Tenant propietario.
        ("Vehiculos.EditarFichaVehiculoCommandHandler", "ITrabajadoresQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IAsignacionesQueryContext"),
        // Encargo de administración pleno (decisión del 2026-10-15): activar la IA por encargo
        // relee la fila del encargo pleno (vigente, del Tenant propietario activo) para guardar
        // su cláusula en la instrucción; no se da por buena la señal del rol efectivo.
        ("Cumplimiento.ActivarTratamientoIaPorEncargoCommandHandler", "IEncargosAdministracionQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ICentrosQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IClientesQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IConfiguracionQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IDocumentosQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IEmpresasQueryContext"),
        // Voluntarios inactivos y requisitos de los eventos futuros o vigentes (tipo de contratación, 2026-10-16).
        ("Alertas.ObtenerAlertasQueryHandler", "IProyectosQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "IRequisitosEventoQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ITiposDocumentoQueryContext"),
        ("Alertas.ObtenerAlertasQueryHandler", "ITrabajadoresQueryContext"),
        ("Alertas.ObtenerSugerenciasPreventivasQueryHandler", "IAsignacionesQueryContext"),
        ("Alertas.ObtenerSugerenciasPreventivasQueryHandler", "ICentrosQueryContext"),
        ("Alertas.ObtenerSugerenciasPreventivasQueryHandler", "ITrabajadoresQueryContext"),
        ("ApiKeys.GenerarClaveApiCommandHandler", "IDelegacionTenantRepository"),
        ("ApiKeys.ObtenerClavesApiQueryHandler", "ITenantsQueryContext"),
        ("ApiKeys.RevocarClaveApiCommandHandler", "IDelegacionTenantRepository"),
        ("ApiKeys.RevocarClaveApiCommandHandler", "ITenantsQueryContext"),
        ("Asignaciones.CrearAsignacionCommandHandler", "ICentrosQueryContext"),
        ("Asignaciones.CrearAsignacionCommandHandler", "ITrabajadoresQueryContext"),
        ("Asignaciones.CrearAsignacionesCommandHandler", "ICentrosQueryContext"),
        ("Asignaciones.CrearAsignacionesCommandHandler", "ITrabajadoresQueryContext"),
        // P1-X2: el acordeón no exige nada a un Centro sin gestión CAE (CentrosSinGestionCae).
        ("Asignaciones.ObtenerAsignacionesDocumentacionPorCentroQueryHandler", "ICentrosQueryContext"),
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
        // ObtenerAuditoriaQuery necesita saber si Cliente/Empresa/Centro/
        // Trabajador/Documento SIGUEN eliminados hoy (no solo lo que dice el
        // JSON histórico) para no ofrecer un "Restaurar" que el
        // Restaurar*Command de cada feature va a rechazar (defecto de Codex,
        // 2026-09-11) — la misma comprobación IgnoreQueryFilters()+TenantId
        // de esos Restaurar*Command, hecha en lote aquí en vez de por fila.
        ("Auditoria.ObtenerAuditoriaQueryHandler", "ICentrosQueryContext"),
        ("Auditoria.ObtenerAuditoriaQueryHandler", "IDocumentosQueryContext"),
        ("Auditoria.ObtenerAuditoriaQueryHandler", "IEmpresasQueryContext"),
        ("Auditoria.ObtenerAuditoriaQueryHandler", "IGestionesQueryContext"),
        ("Auditoria.ObtenerAuditoriaQueryHandler", "IProyectosQueryContext"),
        ("Auditoria.ObtenerAuditoriaQueryHandler", "ITrabajadoresQueryContext"),
        ("Auditoria.ObtenerAuditoriaQueryHandler", "IVehiculosQueryContext"),
        // La pantalla de accesos a documentos sensibles enseña el nombre del Tipo
        // de documento en vez del GUID (revisión UX pre-piloto 2026-09-28, D.1):
        // una lectura en lote por página, con los filtros normales de Documentos
        // (Tenant activo, sin dar de baja) y sin IgnoreQueryFilters().
        ("Auditoria.ObtenerAccesosDocumentosSensiblesQueryHandler", "IDocumentosQueryContext"),
        ("Auditoria.ObtenerAccesosDocumentosSensiblesQueryHandler", "ITiposDocumentoQueryContext"),
        ("Bandeja.ObtenerBandejaGestorQueryHandler", "IConfiguracionQueryContext"),
        // Mismo ParametroSistema (horas de aviso de Visita) que la Bandeja de
        // arriba, leído una vez por Tenant dentro del fan-out de Mi trabajo Gen2.
        ("Bandeja.ObtenerMiTrabajoAgregadoQueryHandler", "IConfiguracionQueryContext"),
        // Razón social y EsPropia de la Empresa sujeto de las tareas de Mi trabajo
        // Gen2 (contrato § 14: «Documentación de empresa» / «Subcontrata · nombre»),
        // una consulta por Tenant dentro del mismo AmbitoTenantExplicito.
        ("Bandeja.ObtenerMiTrabajoAgregadoQueryHandler", "IEmpresasQueryContext"),
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
        // «N documentos vencen esta semana» de «Reclamar a {Empresa}» en la paleta: Documentos de la Empresa y de sus
        // Trabajadores en cartera, sin voluntarios inactivos (Proyectos), con las reglas de Documentos.Operativos().
        ("BusquedaGlobal.ContarVencenEstaSemanaDeEmpresaQueryHandler", "IDocumentosQueryContext"),
        ("BusquedaGlobal.ContarVencenEstaSemanaDeEmpresaQueryHandler", "IProyectosQueryContext"),
        ("BusquedaGlobal.ContarVencenEstaSemanaDeEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "IConfiguracionQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "IDocumentosQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "ITiposDocumentoQueryContext"),
        ("Calendario.ObtenerVencimientosMesQueryHandler", "ITrabajadoresQueryContext"),
        // Semana por Empresa compone vencimientos visibles y el historial de envío; no escribe fuera de Calendario.
        ("Calendario.ObtenerSemanaPorEmpresaQueryHandler", "IDocumentosQueryContext"),
        ("Calendario.ObtenerSemanaPorEmpresaQueryHandler", "IEmpresasQueryContext"),
        ("Calendario.ObtenerSemanaPorEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Calendario.ObtenerSemanaPorEmpresaQueryHandler", "IProyectosQueryContext"),
        ("Calendario.ObtenerSemanaPorEmpresaQueryHandler", "IReclamacionesQueryContext"),
        ("Calendario.ObtenerSemanaPorEmpresaQueryHandler", "ITiposDocumentoQueryContext"),
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
        // Opciones del filtro «Empresa» de /centros: las Empresas de los Centros visibles
        // (rediseño de listados, fase 1). Mismo par de contextos que ObtenerCentrosQueryHandler.
        ("Centros.ObtenerEmpresasDeCentrosVisiblesQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IClientesQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IDocumentosQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerDocumentacionBloqueantePendienteQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.ObtenerDocumentacionRequeridaDeCentroQueryHandler", "ITiposDocumentoQueryContext"),
        // La misma lectura que la de arriba, sobre todos los Centros del Cliente empresarial (Cliente 360, «Documentación»).
        ("Centros.ObtenerDocumentacionExigidaDeClienteQueryHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "ISubcontratasQueryContext"),
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "ITrabajadoresQueryContext"),
        // Quien entra en un Centro por un evento futuro o vigente (Proyecto de tipo Evento) figura sin Asignación (2026-10-15).
        ("Centros.ObtenerTrabajadoresAsignadosDeCentroQueryHandler", "IProyectosQueryContext"),
        // Documentación por Empresa empleadora en un Centro: Asignaciones activas, titulares visibles,
        // documentos operativos y umbrales. Requisitos y tolerancias reutilizan su lectura autorizada.
        ("Centros.ObtenerDocumentacionEmpresasDeCentroQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerDocumentacionEmpresasDeCentroQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.ObtenerDocumentacionEmpresasDeCentroQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerDocumentacionEmpresasDeCentroQueryHandler", "IDocumentosQueryContext"),
        ("Centros.ObtenerDocumentacionEmpresasDeCentroQueryHandler", "IConfiguracionQueryContext"),
        // Vista «Vehículos» de Centro 360: los Vehículos los pide a ObtenerVehiculosConActividadDeCentroQuery (mismo
        // alcance); lee sus documentos operativos, el nombre de su tipo y los umbrales, y la Empresa contratista de cada
        // Subcontrata (Relación Empresarial vigente, Asignaciones activas del Centro y sus empleadoras).
        ("Centros.ObtenerDocumentacionVehiculosDeCentroQueryHandler", "IAsignacionesQueryContext"),
        ("Centros.ObtenerDocumentacionVehiculosDeCentroQueryHandler", "IConfiguracionQueryContext"),
        ("Centros.ObtenerDocumentacionVehiculosDeCentroQueryHandler", "IDocumentosQueryContext"),
        ("Centros.ObtenerDocumentacionVehiculosDeCentroQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerDocumentacionVehiculosDeCentroQueryHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerDocumentacionVehiculosDeCentroQueryHandler", "ITrabajadoresQueryContext"),

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
        // Plataforma CAE de Cliente 360 (2026-10-18): la plataforma es del Cliente empresarial y el acceso
        // de cada Empresa proveedora, las dos tablas viven en Empresas; el Cliente empresarial es una Empresa.
        ("Clientes.GuardarAccesoPlataformaCaeCommandHandler", "IEmpresasQueryContext"),
        ("Clientes.GuardarAccesoPlataformaCaeCommandHandler", "IPlataformaCaeClienteEmpresarialRepository"),
        ("Clientes.GuardarPlataformaCaeClienteCommandHandler", "IEmpresaRepository"),
        // Comprueba que la Empresa es hoy Cliente empresarial (Relación Empresarial vigente).
        ("Clientes.GuardarPlataformaCaeClienteCommandHandler", "IEmpresasQueryContext"),
        ("Clientes.GuardarPlataformaCaeClienteCommandHandler", "IPlataformaCaeClienteEmpresarialRepository"),
        ("Clientes.ObtenerAccesosPlataformaCaeClienteQueryHandler", "IEmpresasQueryContext"),
        ("Clientes.ObtenerPlataformaCaeClienteQueryHandler", "IEmpresasQueryContext"),
        // Plataformas CAE de los Centros del Cliente empresarial, para que la caja no diga «sin plataforma» si sus Centros la tienen.
        ("Clientes.ObtenerPlataformaCaeClienteQueryHandler", "ICentrosQueryContext"),
        ("Clientes.ObtenerPlataformaCaeClienteQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Clientes.RevelarContrasenaAccesoPlataformaCaeQueryHandler", "IEmpresasQueryContext"),
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
        ("Subcontratas.RestaurarSubcontrataCommandHandler", "IEmpresasQueryContext"),
        ("Subcontratas.CambiarNivelServicioSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.GuardarCredencialAccesoSubcontrataCommandHandler", "IEmpresaRepository"),
        // «Nota interna» de Subcontrata 360: escribe Empresa.Notas, por el mismo motivo que sus vecinos.
        ("Subcontratas.GuardarNotaInternaSubcontrataCommandHandler", "IEmpresaRepository"),
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "IEmpresaRepository"),
        // F3b-Subcontrata: el detalle de Subcontrata (RazonSocial/Cif/NivelServicio)
        // ahora vive en Empresas; ISubcontratasQueryContext se conserva en este
        // handler solo por SubcontratasClientes/SubcontratasEmpresas.
        ("Subcontratas.ObtenerSubcontrataPorIdQueryHandler", "IEmpresasQueryContext"),
        // F3b-Subcontrata (revisión adversaria, 2026-08-26): adelantadas por
        // evidencia real, no por reclasificación — ver
        // Project-Hydra-Negocio/tecnico/f3b-subcontrata-obtenersubcontratasquery-adelantada-2026-08-26.md
        // y Project-Hydra-Negocio/tecnico/f3b-subcontrata-selector-adelantado-2026-08-26.md.
        ("Subcontratas.ObtenerSubcontratasQueryHandler", "IEmpresasQueryContext"),
        // La identidad del listado muestra los Trabajadores de la Empresa subcontratista:
        // recuento y ventana salen de la misma lectura, acotada por su ámbito efectivo y por RLS.
        ("Subcontratas.ObtenerSubcontratasQueryHandler", "ITrabajadoresQueryContext"),
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
        // Rellena variables con los sujetos y la visita del hilo; solo lee otras features.
        ("Comunicaciones.AplicarMacroCommandHandler", "ICentrosQueryContext"),
        ("Comunicaciones.AplicarMacroCommandHandler", "IDocumentosQueryContext"),
        ("Comunicaciones.AplicarMacroCommandHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.AplicarMacroCommandHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.AplicarMacroCommandHandler", "ITrabajadoresQueryContext"),
        ("Comunicaciones.AplicarMacroCommandHandler", "IVisitasQueryContext"),
        ("Comunicaciones.AsignarClienteConversacionCommandHandler", "IEmpresaRepository"),
        ("Comunicaciones.CrearMacroCommandHandler", "IEmpresaRepository"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "IEmpresasQueryContext"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "ITiposDocumentoQueryContext"),
        ("Comunicaciones.DetectarActualizacionDocumentoDesdeAdjuntoQueryHandler", "ITrabajadoresQueryContext"),
        ("Comunicaciones.EditarMacroCommandHandler", "IEmpresaRepository"),
        ("Comunicaciones.EnviarMensajeNuevoCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.MigrarConversacionACorreoCommandHandler", "IConexionIntegracionRepository"),
        ("Comunicaciones.MigrarConversacionACorreoCommandHandler", "IIntegracionesQueryContext"),
        // Alerta crítica (Z5-1, 2026-10-23): «Registrar incidencia» ata la alerta a la Incidencia que acaba de crearse y el Deshacer
        // la elimina, devuelve la alerta a Pendiente y retira los avisos «derivado» en una sola transacción; por eso los dos
        // handlers leen la Incidencia (con su alcance de gestión) y el Deshacer marca como leídas las NotificacionUsuario.
        ("Comunicaciones.DeshacerDerivacionAlertaCriticaCommandHandler", "IIncidenciaRepository"),
        ("Comunicaciones.DeshacerDerivacionAlertaCriticaCommandHandler", "INotificacionUsuarioRepository"),
        ("Comunicaciones.ResolverAlertaCriticaCommandHandler", "IIncidenciaRepository"),
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
        // Cumplimiento del Tenant beneficiario en contexto Empresa, medido como /empresas: Empresas propias, Centros de sus Asignaciones activas.
        ("Dashboard.ObtenerCumplimientoExigidoTenantQueryHandler", "IAsignacionesQueryContext"),
        ("Dashboard.ObtenerCumplimientoExigidoTenantQueryHandler", "IEmpresasQueryContext"),
        ("Dashboard.ObtenerCumplimientoExigidoTenantQueryHandler", "ITrabajadoresQueryContext"),
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
        // Cuadro de dirección (incremento 2): recuentos del mes por Tenant beneficiario.
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IComunicacionesQueryContext"),
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IReclamacionesQueryContext"),
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "ITrabajadoresQueryContext"),
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IGestionesQueryContext"),
        // Obras nuevas y Centros nuevos por Cliente empresarial del mes (decisión de Chris, 2026-10-20).
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IProyectosQueryContext"),
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IEmpresasQueryContext"),
        // Visitas de Trabajador extranjero del mes: unidades de la tarifa del Operador CAE (decisión de Chris, 2026-10-20).
        ("Dashboard.ObtenerCuadroDireccionQueryHandler", "IVisitasQueryContext"),
        // Solo comprueba que el Operador CAE opera hoy al Tenant beneficiario antes de fijarle una tarifa.
        ("Dashboard.FijarTarifaOperadorCaeCommandHandler", "IOperacionesQueryContext"),
        // Acota la valoración del Operador CAE a la vigencia de su Asignación de Operación en cada mes.
        ("Dashboard.ObtenerSerieCuadroDireccionQueryHandler", "IOperacionesQueryContext"),
        ("Dashboard.ObtenerFacturacionOperadorCaeQueryHandler", "IOperacionesQueryContext"),
        ("Dashboard.ObtenerEmpresasGestionadasDelMesQueryHandler", "IOperacionesQueryContext"),
        // Serie y Comunicaciones del Cuadro de dirección: conversaciones del mes.
        ("Dashboard.ObtenerComunicacionesCuadroDireccionQueryHandler", "IComunicacionesQueryContext"),
        ("Dashboard.ObtenerGestionesPorDiaCuadroDireccionQueryHandler", "IGestionesQueryContext"),
        // Exportación de Trabajadores extranjeros del mes (decisión de Chris, 2026-10-20): cada fila junta la asignación
        // a la Visita, el Trabajador, el Centro, las razones sociales y, para las asignaciones antiguas, su alta auditada.
        ("Dashboard.ObtenerExtranjerosDelMesQueryHandler", "IAuditoriaQueryContext"),
        ("Dashboard.ObtenerExtranjerosDelMesQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerExtranjerosDelMesQueryHandler", "IEmpresasQueryContext"),
        ("Dashboard.ObtenerExtranjerosDelMesQueryHandler", "ITrabajadoresQueryContext"),
        ("Dashboard.ObtenerExtranjerosDelMesQueryHandler", "IVisitasQueryContext"),
        ("Dashboard.ObtenerEstadisticasAprobacionDocumentoQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IClientesQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IComunicacionesQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IConfiguracionQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "ITelemetriaQueryContext"),
        ("Dashboard.ObtenerKpisBpoQueryHandler", "IVisitasQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "ICentrosQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IConfiguracionQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IDocumentosQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IEmpresasQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "IProyectosQueryContext"),
        ("Dashboard.ObtenerKpisDashboardQueryHandler", "ITiposDocumentoQueryContext"),
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
        ("Documentos.ConfirmarVigenciaAcreditacionCommandHandler", "IProyectosQueryContext"),
        ("Documentos.CorregirRevisionIaDocumentoCommandHandler", "IAuditoriaExtraccionIaRepository"),
        ("Documentos.CorregirRevisionIaDocumentoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.CorregirRevisionIaDocumentoCommandHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerRevisionesIaPendientesQueryHandler", "IDocumentosIaQueryContext"),
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
        // Asignación de Cartera al restaurar: DocumentoVisibleAsync resuelve el Cliente empresarial
        // de un Documento de Proyecto, igual que al eliminarlo.
        ("Documentos.RestaurarDocumentoCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionAceptadaCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionPendienteDeSubirCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionRechazadaCommandHandler", "IProyectosQueryContext"),
        ("Documentos.RestaurarAnotacionAcreditacionCommandHandler", "IProyectosQueryContext"),
        // Centro 360, cambiar a mano el estado en la plataforma (bloque 3 del 21-10): los dos comandos comprueban que la
        // acreditación es de un canal del Centro (ICentrosQueryContext) y que su Documento se ve en la cartera
        // (IProyectosQueryContext, como el resto de comandos de acreditación). El Historial del Centro pinta, en una lectura
        // por lote, el documento, su titular (Trabajador, Empresa o Vehículo) y la plataforma de cada cambio.
        ("Documentos.CambiarEstadoAcreditacionEnCentroCommandHandler", "ICentrosQueryContext"),
        ("Documentos.CambiarEstadoAcreditacionEnCentroCommandHandler", "IProyectosQueryContext"),
        ("Documentos.DeshacerCambioEstadoAcreditacionCommandHandler", "ICentrosQueryContext"),
        ("Documentos.DeshacerCambioEstadoAcreditacionCommandHandler", "IProyectosQueryContext"),
        ("Centros.ObtenerCambiosEstadoPlataformaDeCentroQueryHandler", "IDocumentosQueryContext"),
        ("Centros.ObtenerCambiosEstadoPlataformaDeCentroQueryHandler", "ITiposDocumentoQueryContext"),
        ("Centros.ObtenerCambiosEstadoPlataformaDeCentroQueryHandler", "ITrabajadoresQueryContext"),
        ("Centros.ObtenerCambiosEstadoPlataformaDeCentroQueryHandler", "IEmpresasQueryContext"),
        ("Centros.ObtenerCambiosEstadoPlataformaDeCentroQueryHandler", "IVehiculosQueryContext"),
        ("Centros.ObtenerCambiosEstadoPlataformaDeCentroQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        // MVP2 § 14.5 (kill switch remoto, ver Project-Hydra-Negocio/tecnico/ARQUITECTURA-INTEGRACIONES.md en
        // el repositorio de negocio): «Lo he enviado» de la extensión necesita resolver
        // el proveedor del canal de la acreditación (ICentrosQueryContext) y
        // comprobar su Activo (IProveedoresPlataformaCaeQueryContext). «Marcar
        // subido» del drill-down interno no exige esta comprobación y no depende
        // de ninguna de las dos.
        ("Documentos.ConfirmarSubidaDesdeExtensionCommandHandler", "ICentrosQueryContext"),
        ("Documentos.ConfirmarSubidaDesdeExtensionCommandHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Documentos.ConfirmarSubidaDesdeExtensionCommandHandler", "IProyectosQueryContext"),
        ("Documentos.DeshacerSubidaDesdeExtensionCommandHandler", "IProyectosQueryContext"),
        ("Documentos.MarcarAcreditacionSubidaCommandHandler", "IProyectosQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "ICentrosQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "IClientesQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerAcreditacionesPorProveedorQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.ObtenerPlataformasEnUsoQueryHandler", "ICentrosQueryContext"),
        ("Documentos.ObtenerPlataformasEnUsoQueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Documentos.ObtenerDocumentacionBaseTrabajadoresQueryHandler", "IConfiguracionQueryContext"),
        ("Documentos.ObtenerDocumentacionBaseTrabajadoresQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IClientesQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IProyectosQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "ITrabajadoresQueryContext"),
        ("Documentos.ObtenerDocumentoPorIdQueryHandler", "IVehiculosQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "IAsignacionesQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "ICentrosQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "IConfiguracionQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "IEmpresasQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "IProveedoresPlataformaCaeQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "ITiposDocumentoQueryContext"),
        ("Documentos.ObtenerFichaDocumento360QueryHandler", "ITrabajadoresQueryContext"),
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
        ("Empresas.ObtenerVigenciaEmpresaQueryHandler", "IAsignacionesQueryContext"),
        ("Empresas.ObtenerVigenciaEmpresaQueryHandler", "ICentrosQueryContext"),
        ("Empresas.ObtenerVigenciaEmpresaQueryHandler", "IConfiguracionQueryContext"),
        ("Empresas.ObtenerVigenciaEmpresaQueryHandler", "IDocumentosQueryContext"),
        ("Empresas.ObtenerVigenciaEmpresaQueryHandler", "ITiposDocumentoQueryContext"),
        ("Empresas.ObtenerVigenciaEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Empresas.ObtenerCentrosConActividadDeEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Empresas.ObtenerClientesDeEmpresaQueryHandler", "IClientesQueryContext"),
        ("Empresas.ObtenerCumplimientoEmpresaQueryHandler", "IAsignacionesQueryContext"),
        ("Empresas.ObtenerCumplimientoEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        ("Empresas.ObtenerEmpresasQueryHandler", "IAsignacionesQueryContext"),
        // Módulo 8 § 4.1 (2026-09-17): filtrar/ordenar por EstadoDocumental
        // pasa de materializar todas las Empresas visibles a una subconsulta
        // correlacionada (MIN por propietario) contra Documentos, con los
        // umbrales de ParametroSistema — mismo patrón que ObtenerDocumentosQuery.
        ("Empresas.ObtenerEmpresasQueryHandler", "IConfiguracionQueryContext"),
        ("Empresas.ObtenerEmpresasQueryHandler", "IDocumentosQueryContext"),
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
        // Asignación de Cartera: el ítem sugerido y el mensaje de origen solo se enlazan si la Conversación es visible.
        ("Gestiones.CrearGestionesParaTrabajadorCommandHandler", "IComunicacionesQueryContext"),
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
        // Soporte TALVEG universal (ADR-011 § 8.9): una concesión global de
        // SoporteLectura cubre cualquier Guid, así que la apertura comprueba que
        // el Tenant objetivo existe. Solo lee Tenants, nunca contenido del Tenant.
        ("Plataforma.AbrirSesionPrivilegiadaCommandHandler", "ITenantsQueryContext"),
        ("Proyectos.AsignarTecnicoProyectoCommandHandler","ITrabajadoresQueryContext"),
        ("Proyectos.CrearProyectoCommandHandler", "ICentrosQueryContext"),
        // Alta de un contrato de acceso (Inbound M1): como CrearProyecto, comprueba que el Centro es del Cliente y que la
        // Empresa contratista existe en el Tenant, solo por su Id.
        ("ContratosAcceso.CrearContratoAccesoCommandHandler", "ICentrosQueryContext"),
        ("ContratosAcceso.CrearContratoAccesoCommandHandler", "IEmpresasQueryContext"),
        // Autorización de lectura documental (Inbound M1): comprueba que los Trabajadores asignados existen en el Tenant,
        // solo por su Id, igual que AsignarTecnicoProyecto.
        ("AutorizacionesLectura.ConcederAutorizacionLecturaCommandHandler", "ITrabajadoresQueryContext"),
        ("AutorizacionesLectura.AsignarTrabajadoresAutorizacionCommandHandler", "ITrabajadoresQueryContext"),
        // Copias de evidencia validada (Inbound M1, PR 4): la entidad vive en su propio espacio de dominio
        // (Domain.EvidenciasInbound) y sus handlers en el de los contratos de acceso, porque la autoridad es la del
        // contrato. El registro valida el Tipo de Documento del catálogo propio y toma el sujeto de un Documento de
        // Empresa de la Empresa contratista local del contrato, solo por su Id; las consultas nombran el tipo.
        ("ContratosAcceso.RegistrarEvidenciaInboundCommandHandler", "IEvidenciaInboundRepository"),
        ("ContratosAcceso.RegistrarEvidenciaInboundCommandHandler", "ITiposDocumentoQueryContext"),
        ("ContratosAcceso.RegistrarEvidenciaInboundCommandHandler", "IEmpresasQueryContext"),
        ("ContratosAcceso.RechazarEvidenciaInboundCommandHandler", "IEvidenciaInboundRepository"),
        ("ContratosAcceso.ValidarEvidenciaInboundCommandHandler", "IEvidenciaInboundRepository"),
        ("ContratosAcceso.ObtenerEvidenciasInboundQueryHandler", "ITiposDocumentoQueryContext"),
        ("ContratosAcceso.ObtenerEvidenciaInboundPorIdQueryHandler", "ITiposDocumentoQueryContext"),
        // Requisitos documentales de un evento: el Tipo de Documento se valida y se nombra desde su lectura.
        ("Proyectos.AnadirRequisitoEventoCommandHandler", "ITiposDocumentoQueryContext"),
        ("Proyectos.ObtenerRequisitosEventoQueryHandler", "ITiposDocumentoQueryContext"),
        // Al copiar requisitos de otro evento o de una plantilla, un tipo archivado pasa a su sustituto (catálogo 2026-10-17).
        ("Proyectos.CrearProyectoCommandHandler", "ITiposDocumentoQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "IClientesQueryContext"),
        ("Proyectos.ObtenerProyectoPorIdQueryHandler", "IDocumentosQueryContext"),
        ("Proyectos.ObtenerProyectosParaSelectorQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerProyectosParaSelectorQueryHandler", "IClientesQueryContext"),
        ("Proyectos.ObtenerProyectosQueryHandler", "ICentrosQueryContext"),
        // Nombres de los técnicos activos de cada fila del listado: el mismo cruce Proyectos → Trabajadores
        // que ya tiene ObtenerTecnicosProyectoQueryHandler, en una sola consulta para toda la lista.
        ("Proyectos.ObtenerProyectosQueryHandler", "ITrabajadoresQueryContext"),
        ("Proyectos.ObtenerTecnicosProyectoQueryHandler", "ITrabajadoresQueryContext"),
        // Lecturas derivadas del Proyecto: empleadores de técnicos activos
        // y Visitas en su periodo. Contexts de lectura con Tenant propietario y alcance.
        ("Proyectos.ObtenerEmpresasProyectoQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerEmpresasProyectoQueryHandler", "IEmpresasQueryContext"),
        ("Proyectos.ObtenerEmpresasProyectoQueryHandler", "ITrabajadoresQueryContext"),
        ("Proyectos.ObtenerVisitasProyectoQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerVisitasProyectoQueryHandler", "IVisitasQueryContext"),
        // Vehículos de las Empresas empleadoras del Proyecto (no hay relación Vehículo-Proyecto): misma derivación
        // que Empresas del Proyecto, más la lectura de Vehículos con el alcance de Vehículos visibles.
        ("Proyectos.ObtenerVehiculosProyectoQueryHandler", "ICentrosQueryContext"),
        ("Proyectos.ObtenerVehiculosProyectoQueryHandler", "IEmpresasQueryContext"),
        ("Proyectos.ObtenerVehiculosProyectoQueryHandler", "ITrabajadoresQueryContext"),
        ("Proyectos.ObtenerVehiculosProyectoQueryHandler", "IVehiculosQueryContext"),

        ("Reclamaciones.EnviarReclamacionCommandHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ICentrosQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IClientesQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.EnviarReclamacionCommandHandler", "ITrabajadoresQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaCommandHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaCommandHandler", "ITiposDocumentoQueryContext"),
        // «Gestionar» a la Empresa empleadora (2026-10-18): revalida el mismo join reclamable que EnviarReclamacionCommandHandler
        // (Documento, Trabajador, Tipo, Asignación activa, Centro con gestión CAE) y lee la Empresa titular, sin el Cliente.
        ("Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler", "ICentrosQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler", "IDocumentosQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler", "ITiposDocumentoQueryContext"),
        ("Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler", "ITrabajadoresQueryContext"),
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
        // Reclamación preventiva de Empresa 360 (2026-10-19): ofrece los Centros con gestión CAE donde la Empresa tiene Trabajadores
        // con Asignación activa, con el mismo alcance que ObtenerLoteReclamacionQueryHandler, y las sedes y Oficios de la Empresa.
        ("Reclamaciones.ObtenerReclamacionPreventivaEmpresaQueryHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.ObtenerReclamacionPreventivaEmpresaQueryHandler", "ICentrosQueryContext"),
        ("Reclamaciones.ObtenerReclamacionPreventivaEmpresaQueryHandler", "IEmpresasQueryContext"),
        ("Reclamaciones.ObtenerReclamacionPreventivaEmpresaQueryHandler", "ITrabajadoresQueryContext"),
        // Reclamación por acceso (2026-10-19): solo cuentan los Trabajadores pedidos que son de la Empresa (Trabajador.EmpresaId).
        ("Reclamaciones.ObtenerReclamacionPorAccesoQueryHandler", "ITrabajadoresQueryContext"),
        // Lo que el Centro exige de la propia Empresa (2026-10-21: lo exigido, no solo lo que bloquea): Centro con gestión CAE,
        // Asignación activa de alguno de los Trabajadores elegidos y ResolucionTipoDocumentoCentro sobre los tipos de ámbito Empresa.
        ("Reclamaciones.ObtenerReclamacionPorAccesoQueryHandler", "IAsignacionesQueryContext"),
        ("Reclamaciones.ObtenerReclamacionPorAccesoQueryHandler", "ICentrosQueryContext"),
        ("Reclamaciones.ObtenerReclamacionPorAccesoQueryHandler", "ITiposDocumentoQueryContext"),
        // «Pendiente de subir a plataformas» de Empresa 360 (2026-10-19): acota lo bloqueante por Centro a los Trabajadores de la
        // Empresa y a sus filtros de Delegación y Oficio, que solo conoce el catálogo de Trabajadores.
        ("Reclamaciones.ObtenerPendientesDeSubirAPlataformaEmpresaQueryHandler", "ITrabajadoresQueryContext"),
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
        // Voluntarios inactivos (sin evento vigente ni futuro) no cuentan como personal para la serie documental (2026-10-16).
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "IProyectosQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "ITiposDocumentoQueryContext"),
        ("Subcontratas.ObtenerSupervisionSubcontrataQueryHandler", "ITrabajadoresQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IAsignacionesQueryContext"),
        // P1-X2: un Centro sin gestión CAE no exige nada al personal de la subcontrata (CentrosSinGestionCae).
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "ICentrosQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IConfiguracionQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IDocumentosQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "ITiposDocumentoQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "ITrabajadoresQueryContext"),
        // Mismo universo que CalculoEstadoSubcontrataService: voluntarios inactivos y requisitos de los eventos futuros o vigentes (2026-10-16).
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IProyectosQueryContext"),
        ("Subcontratas.ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler", "IRequisitosEventoQueryContext"),
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "ICentrosQueryContext"),
        // Revisión Codex 2026-09-11: el Centro tiene que estar en una RelacionEmpresarial
        // vigente de esta Subcontrata, mismo criterio que CentrosSeleccionables de
        // ObtenerSupervisionSubcontrataQuery.
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "IEmpresasQueryContext"),
        ("Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler", "ITiposDocumentoQueryContext"),
        ("Telemetria.ObtenerTiempoGestionConversacionQueryHandler", "IConfiguracionQueryContext"),
        ("Telemetria.RegistrarTramoGestionCommandHandler", "IComunicacionesQueryContext"),
        ("Telemetria.RegistrarTramoGestionCommandHandler", "IConfiguracionQueryContext"),
        ("Tenants.AbrirAccesoSoporteCommandHandler", "IRegistroActividadSoporteRepository"),
        ("Tenants.CerrarAccesoSoporteCommandHandler", "IRegistroActividadSoporteRepository"),
        ("Tenants.CrearClienteDeleganteCommandHandler", "IParametroSistemaRepository"),
        ("Tenants.CrearOperadorCaeExternoCommandHandler", "IParametroSistemaRepository"),
        ("Tenants.CrearTenantPropietarioDeOperadorCaeExternoCommandHandler", "IParametroSistemaRepository"),
        ("TiposDocumento.ActualizarDeteccionTrabajadoresGlobalCommandHandler", "ITipoDocumentoRepository"),
        ("TiposDocumento.ActualizarLecturaIaClienteCommandHandler", "IClientesQueryContext"),
        ("TiposDocumento.ActualizarLecturaIaClienteCommandHandler", "IConfiguracionIaDocumentoClienteRepository"),
        ("TiposDocumento.ActualizarLecturaIaGlobalCommandHandler", "ITipoDocumentoRepository"),
        // Tipo de documento 360: el estado de un tipo se pinta por Trabajador y Centro, con el nombre de su Empresa y las fechas de su documento.
        ("TiposDocumento.ObtenerEstadoTipoDocumentoQueryHandler", "ICentrosQueryContext"),
        ("TiposDocumento.ObtenerEstadoTipoDocumentoQueryHandler", "IDocumentosQueryContext"),
        ("TiposDocumento.ObtenerEstadoTipoDocumentoQueryHandler", "IEmpresasQueryContext"),
        ("TiposDocumento.ObtenerEstadoTipoDocumentoQueryHandler", "ITrabajadoresQueryContext"),
        // La tolerancia por defecto del Cliente empresarial (Empresa contraparte) vive junto a los Tipos; se comprueba el alcance como en la lectura por IA.
        ("TiposDocumento.EstablecerToleranciaClienteEmpresarialCommandHandler", "IToleranciaDocumentoClienteEmpresarialRepository"),
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
        ("Trabajadores.ObtenerDeteccionesPorEmpresaQueryHandler", "IAsignacionesQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IAsignacionesQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "ICentrosQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IClientesQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IConfiguracionQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IDocumentosQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "ITiposDocumentoQueryContext"),
        // Módulo 8 § 4.1 (2026-09-17): mismo motivo que Empresas.ObtenerEmpresasQueryHandler arriba.
        // Opciones de la pastilla «Empresa» de /trabajadores: los empleadores de los Trabajadores visibles.
        ("Trabajadores.ObtenerEmpleadoresDeTrabajadoresVisiblesQueryHandler", "IEmpresasQueryContext"),
        // Filtro «Centro» de /trabajadores: Trabajadores con una Asignación activa en ese Centro (lectura, EXISTS
        // correlacionado en la misma consulta). Solo estrecha: se compone con el alcance.
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "IAsignacionesQueryContext"),
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "IConfiguracionQueryContext"),
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "IDocumentosQueryContext"),
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "ISubcontratasQueryContext"),
        // Filtro e indicador de voluntario inactivo (tipo de contratación, 2026-10-16).
        ("Trabajadores.ObtenerTrabajadoresQueryHandler", "IProyectosQueryContext"),
        ("Trabajadores.ObtenerTrabajadorPorIdQueryHandler", "IEmpresasQueryContext"),
        // P4 (2026-09-23): sin DNI, la etiqueta del selector desempata homónimos por el empleador
        // (razón social de su Empresa o Subcontrata), proyectado con LEFT JOIN en la misma consulta.
        ("Trabajadores.ObtenerTrabajadoresParaSelectorQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerTrabajadorPorIdQueryHandler", "ISubcontratasQueryContext"),
        ("Vehiculos.CrearVehiculoCommandHandler", "IEmpresasQueryContext"),
        ("Vehiculos.CrearVehiculoCommandHandler", "ISubcontratasQueryContext"),
        ("Vehiculos.ObtenerVehiculoPorIdQueryHandler", "IEmpresasQueryContext"),
        ("Vehiculos.ObtenerVehiculoPorIdQueryHandler", "ISubcontratasQueryContext"),
        // Módulo 8 § 4.1 (2026-09-17): mismo motivo que Empresas.ObtenerEmpresasQueryHandler arriba.
        ("Vehiculos.ObtenerVehiculosQueryHandler", "IConfiguracionQueryContext"),
        ("Vehiculos.ObtenerVehiculosQueryHandler", "IDocumentosQueryContext"),
        ("Vehiculos.ObtenerVehiculosQueryHandler", "IEmpresasQueryContext"),
        ("Vehiculos.ObtenerVehiculosQueryHandler", "ISubcontratasQueryContext"),
        ("Visitas.CrearVisitaCommandHandler", "ICentrosQueryContext"),
        ("Visitas.CrearVisitaCommandHandler", "IComunicacionesQueryContext"),
        ("Visitas.CrearVisitaCommandHandler", "ISugerenciaVisitaCorreoRepository"),
        ("Visitas.CrearVisitaCommandHandler", "ITrabajadoresQueryContext"),
        ("Visitas.EditarVisitaCommandHandler", "ITrabajadoresQueryContext"),
        // Añadir un Trabajador desde el panel comprueba que existe en el Tenant, igual que EditarVisita.
        ("Visitas.AnadirTrabajadorAVisitaCommandHandler", "ITrabajadoresQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "IClientesQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "IConfiguracionQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerDetalleVisitaQueryHandler", "ITrabajadoresQueryContext"),
        // P1-X2: el aviso de visita de un Centro sin gestión CAE lee el Centro (nombre,
        // modalidad), las Empresas (proveedora y la de cada Trabajador) y los
        // Trabajadores (solo nombre y apellidos), igual que ObtenerDetalleVisita.
        ("Visitas.ObtenerAvisoVisitaQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerAvisoVisitaQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerAvisoVisitaQueryHandler", "ITrabajadoresQueryContext"),
        // P1-X1: la solicitud de acceso de un Centro gestionado por correo lee el Centro y
        // sus canales (nombre, destinatarios, contacto), la Empresa y los Trabajadores de la
        // Visita (solo nombre, apellidos y razón social), igual que el aviso de P1-X2; la
        // descarga del zip solo lee el Centro y sus canales para decidir si se ofrece.
        ("Visitas.ObtenerSolicitudAccesoCorreoQueryHandler", "ICentrosQueryContext"),
        ("Visitas.ObtenerSolicitudAccesoCorreoQueryHandler", "IEmpresasQueryContext"),
        ("Visitas.ObtenerSolicitudAccesoCorreoQueryHandler", "ITrabajadoresQueryContext"),
        ("Visitas.ObtenerPaqueteDocumentalVisitaQueryHandler", "ICentrosQueryContext"),
        // La marca manual de «documentación gestionada» no se admite en un Centro sin
        // gestión CAE (la columna no tendría dónde pintarla): lee Centro.GestionCae.
        ("Visitas.MarcarDocumentacionGestionadaCommandHandler", "ICentrosQueryContext"),
        ("Visitas.EnviarPaqueteAcreditacionVisitaCommandHandler", "ICentrosQueryContext"),
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
        // El recuento del listado abre quién entra: nombre y apellidos, como ObtenerDetalleVisita.
        ("Visitas.ObtenerVisitasQueryHandler", "ITrabajadoresQueryContext"),
        // «Anteriores» de Centro 360 nombra a los Trabajadores de cada Visita pasada, acotados a los visibles (ObtenerTrabajadorIdsVisiblesAsync).
        ("Visitas.ObtenerVisitasAnterioresDeCentroQueryHandler", "ITrabajadoresQueryContext"),

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
        // Entradas explícitas, porque valen más que un patrón que no las veía: durante
        // meses fueron referencias cruzadas reales, sin lista y sin ratchet, porque el
        // nombre no acababa en Repository ni QueryContext. CrearCliente y
        // ReasignarEjecutivoCliente salieron de la lista con D-7 (2026-10-02): la
        // referencia EjecutivoUsuarioId ya no escribe carteras.
        ("Tenants.CrearAsignacionOperadorDelegadoCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearClienteDeleganteCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearDelegacionTenantCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearOperadorCaeExternoCommandHandler", "IAsignacionesOperativasWriter"),
        ("Tenants.CrearTenantPropietarioDeOperadorCaeExternoCommandHandler", "IAsignacionesOperativasWriter"),
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
        // ver Project-Hydra-Negocio/tecnico/f3b-selectores-adelantados-2026-08-26.md.
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
        // El listado no obliga a elegir Cliente empresarial: cada fila dice el suyo (razón social). El
        // join es interno: el Proyecto cuyo Cliente empresarial está dado de baja no se lista. El detalle
        // (ObtenerProyectoPorIdQueryHandler) no hace lo mismo: lee la razón social con FirstAsync y lanza.
        ("Proyectos.ObtenerProyectosQueryHandler", "IEmpresasQueryContext"),
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
        ("TiposDocumento.EstablecerToleranciaClienteEmpresarialCommandHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler", "IEmpresasQueryContext"),
        ("Trabajadores.ResolverDeteccionAusenteCommandHandler", "IAsignacionRepository"),
        ("Visitas.ObtenerVisitasParaCalendarioQueryHandler", "IEmpresasQueryContext"),

        // Solicitud de incorporación a cartera (2026-09-22): el Coordinador CAE
        // y el Gestor CAE ven el Tenant propietario rotulado «Empresa» por su
        // nombre (ITenantsQueryContext), y la resolución avisa al Gestor CAE
        // con la misma NotificacionUsuario que ya usa ReasignarEjecutivoCliente.
        ("Operaciones.AceptarSolicitudIncorporacionCarteraCommandHandler", "INotificacionUsuarioRepository"),
        ("Operaciones.AceptarSolicitudIncorporacionCarteraCommandHandler", "ITenantsQueryContext"),
        ("Operaciones.ObtenerSolicitudesIncorporacionCarteraQueryHandler", "ITenantsQueryContext"),
        ("Operaciones.RechazarSolicitudIncorporacionCarteraCommandHandler", "INotificacionUsuarioRepository"),
        ("Operaciones.RechazarSolicitudIncorporacionCarteraCommandHandler", "ITenantsQueryContext"),
        ("Operaciones.RevocarIncorporacionCarteraCommandHandler", "INotificacionUsuarioRepository"),
        ("Operaciones.RevocarIncorporacionCarteraCommandHandler", "ITenantsQueryContext"),

        // Propuesta de apoyo (2026-10-08): el destinatario y quien la propone ven
        // el Tenant propietario rotulado «Empresa» por su nombre, igual que en la
        // solicitud de incorporación. Solo lee el nombre de los Tenants de las
        // propuestas que el repositorio ya acotó al Operador CAE de origen.
        ("Operaciones.ObtenerPropuestasApoyoPendientesQueryHandler", "ITenantsQueryContext"),

        // Aviso de apoyo aceptado o terminado (2026-10-09, I4): el control del
        // Coordinador CAE sobre quién entra en un Tenant propietario es posterior, y
        // es este aviso. Como en la incorporación a cartera, es una notificación
        // sellada con el Tenant de origen del Operador CAE que nombra el Tenant
        // propietario por su nombre (solo lee el de la propuesta ya cargada).
        ("Operaciones.AceptarPropuestaApoyoCarteraCommandHandler", "INotificacionUsuarioRepository"),
        ("Operaciones.AceptarPropuestaApoyoCarteraCommandHandler", "ITenantsQueryContext"),
        ("Operaciones.TerminarApoyoCarteraCommandHandler", "INotificacionUsuarioRepository"),
        ("Operaciones.TerminarApoyoCarteraCommandHandler", "ITenantsQueryContext"),
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
