using System.Reflection;
using CaeManager.Application.AsistenteIa.Tareas;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.AplicarDeteccionIaDocumento;
using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Plantillas.Commands.GenerarDocumentoIndividual;
using CaeManager.Application.Plataforma;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;
using MediatR;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Tercera red de la Asignación de Cartera en escritura: que el handler LLAME a la
/// comprobación, no solo que dependa de ella.</b>
///
/// <para>
/// <see cref="AlcanceDeCarteraEnEscrituraTests"/> y <see cref="EscriturasSobreEntidadesConCarteraTests"/>
/// miran el constructor: un handler que inyecta <see cref="IAlcanceDatosService"/> y nunca lo usa
/// pasa las dos. La retrospectiva de 115 PR (2026-10) encontró unos 20 hallazgos de la revisión
/// puente, 5 de gravedad alta, con esa forma exacta: el comando de escritura de una característica
/// con cartera tenía la dependencia y no llamaba a la comprobación en el camino que escribe.
/// </para>
///
/// <para>
/// <b>Población.</b> Las características con cartera no se escriben a mano: son las carpetas de
/// Application (<c>CaeManager.Application.&lt;Característica&gt;</c>) donde algún handler, de
/// lectura o de escritura, inyecta hoy <see cref="IAlcanceDatosService"/>. Dentro de ellas, cada
/// handler de un comando de escritura (<see cref="ICommandBase"/>) tiene que estar en
/// <b>exactamente una</b> de tres listas: <see cref="Llaman"/>, <see cref="Exentos"/> (con su motivo
/// escrito) o <see cref="PendientesDeCorregir"/> (deuda que solo puede encoger). Un comando nuevo
/// que no esté en ninguna pone esto en rojo, con el camino de llamadas medido para que quien lo
/// clasifique vea por dónde llega, o que no llega.
/// </para>
///
/// <para>
/// <b>Cómo se mide «llama».</b> Con el IL compilado (<see cref="GrafoDeLlamadasIl"/>), no con el
/// texto: el método <c>Handle</c> del handler alcanza, directa o transitivamente —máquinas de
/// estados async, lambdas, funciones locales, servicios de Application, Infrastructure o Web—,
/// un método de <see cref="IAlcanceDatosService"/> que responda a la cartera
/// (<see cref="MetodosQueComprueban"/>: las listas de Ids visibles y sus extensiones por entidad; no
/// <c>TieneAccesoTotalAsync</c> ni <c>AlcanzaElTenantEnteroAsync</c>, que no deciden sobre ninguna fila).
/// Una llamada por interfaz cuenta solo si <b>todas</b> sus implementaciones llegan, y hay al menos
/// una. El control positivo del instrumento vive en
/// <see cref="El_grafo_de_IL_distingue_llamar_de_depender"/>, con tipos sintéticos.
/// </para>
///
/// <para>
/// <b>Lo que esto NO demuestra.</b> (1) Que la comprobación se haga <i>antes</i> de escribir, sobre
/// la <i>misma</i> fila que se escribe y en <i>todas</i> las ramas: basta una llamada alcanzable en
/// algún camino; pedir una lista de Ids visibles cuenta aunque el handler no la cruce con la fila que
/// escribe, y una lambda cuenta por referenciarse (<c>ldftn</c>) aunque nunca se invoque. Eso lo
/// prueban los tests de Application de cada handler. (2) Nada de una petición
/// enviada por <see cref="IMediator"/>: el IL no dice qué handler la recibe; por eso esos
/// delegadores van a <see cref="Exentos"/> con el comando al que delegan, y se comprueba que lo
/// construyen y que su handler sí llama. (3) Nada de RLS, del aislamiento entre Tenants ni de la
/// Sesión Privilegiada: Propiedad ≠ Operación ≠ Plataforma; esto mira solo el plano de Operación.
/// (4) Nada de las carpetas sin ningún handler que inyecte el alcance: se congelan con su motivo en
/// <see cref="CaracteristicasSinCartera"/>, para que una carpeta nueva sea una decisión consciente.
/// </para>
/// </summary>
public class LlamadaAlAlcanceDeCarteraEnEscrituraTests
{
    /// <summary>
    /// Handlers cuyo <c>Handle</c> alcanza la comprobación de cartera. Medido sobre
    /// <c>origin/main</c> <c>ef558744c</c> (2026-10-19): 189 de 222.
    /// </summary>
    private static readonly HashSet<string> Llaman =
    [
        // Asignaciones
        "Asignaciones.CrearAsignacionCommandHandler",
        "Asignaciones.CrearAsignacionesCommandHandler",
        "Asignaciones.DarDeBajaAsignacionCommandHandler",
        "Asignaciones.DarDeBajaAsignacionesCommandHandler",
        "Asignaciones.ReactivarAsignacionCommandHandler",

        // Blindaje42
        "Blindaje42.RegistrarRespuestaCertificacionTgssCommandHandler",
        "Blindaje42.SolicitarCertificacionTgssCommandHandler",

        // Centros
        "Centros.CrearCanalGestionCommandHandler",
        "Centros.CrearCentroCommandHandler",
        "Centros.EditarCanalGestionCommandHandler",
        "Centros.EditarCentroCommandHandler",
        "Centros.EditarFichaCentroCommandHandler",
        "Centros.EliminarCanalGestionCommandHandler",
        "Centros.EliminarCentroCommandHandler",
        "Centros.EliminarCentrosCommandHandler",
        "Centros.EliminarDocumentacionRequeridaCentroCommandHandler",
        "Centros.EstablecerDocumentacionRequeridaCentroCommandHandler",
        "Centros.EstablecerGestionCaeCentroCommandHandler",
        "Centros.GuardarNotaInternaCentroCommandHandler",
        "Centros.MarcarCanalGestionPrincipalCommandHandler",
        "Centros.RestaurarCentroCommandHandler",

        // Clientes
        "Clientes.EditarClienteCommandHandler",
        "Clientes.EliminarClienteCommandHandler",
        "Clientes.EliminarClientesCommandHandler",
        "Clientes.GuardarAccesoPlataformaCaeCommandHandler",
        "Clientes.GuardarPlataformaCaeClienteCommandHandler",
        "Clientes.ReasignarEjecutivoClienteCommandHandler",
        "Clientes.RestaurarClienteCommandHandler",

        // Comunicaciones
        "Comunicaciones.ActualizarDocumentoDesdeAdjuntoCommandHandler",
        "Comunicaciones.AplicarMacroCommandHandler",
        "Comunicaciones.AprobarRecordatorioConversacionCommandHandler",
        "Comunicaciones.AsignarClienteConversacionCommandHandler",
        "Comunicaciones.AsignarEjecutivoConversacionCommandHandler",
        "Comunicaciones.CambiarEstadoConversacionCommandHandler",
        "Comunicaciones.CambiarPrioridadConversacionCommandHandler",
        "Comunicaciones.ConfirmarClasificacionRuidoMensajeCommandHandler",
        "Comunicaciones.ConfirmarIntencionConversacionCommandHandler",
        "Comunicaciones.CrearMacroCommandHandler",
        "Comunicaciones.CrearNotaInternaConversacionCommandHandler",
        "Comunicaciones.DescartarRecordatorioConversacionCommandHandler",
        "Comunicaciones.RestaurarRecordatorioConversacionCommandHandler",
        "Comunicaciones.DescartarSugerenciaGestionCorreoCommandHandler",
        "Comunicaciones.DescartarSugerenciaVisitaCorreoCommandHandler",
        "Comunicaciones.EditarMacroCommandHandler",
        "Comunicaciones.EliminarMacroCommandHandler",
        "Comunicaciones.EnviarMensajeNuevoCommandHandler",
        "Comunicaciones.MigrarConversacionACorreoCommandHandler",
        "Comunicaciones.PedirPrioridadValidacionCommandHandler",
        "Comunicaciones.ResolverAlertaCriticaCommandHandler",
        "Comunicaciones.DeshacerDerivacionAlertaCriticaCommandHandler",
        "Comunicaciones.ResponderConversacionCommandHandler",
        "Comunicaciones.ResponderConversacionWhatsAppCommandHandler",
        "Comunicaciones.VincularConversacionCommandHandler",

        // Contactos
        "Contactos.EliminarContactoAgendaCommandHandler",
        "Contactos.GuardarContactoAgendaCommandHandler",

        // Documentos
        "Documentos.AplicarDeteccionIaDocumentoCommandHandler",
        "Documentos.ConfirmarVigenciaAcreditacionCommandHandler",
        "Documentos.CorregirRevisionIaDocumentoCommandHandler",
        "Documentos.CrearDocumentoCommandHandler",
        "Documentos.EliminarDocumentoCommandHandler",
        "Documentos.EliminarDocumentosCommandHandler",
        "Documentos.FirmarDocumentoEnCampoCommandHandler",
        "Documentos.GuardarSelloEmpresaCommandHandler",
        "Documentos.MarcarAcreditacionAceptadaCommandHandler",
        "Documentos.MarcarAcreditacionPendienteDeSubirCommandHandler",
        "Documentos.MarcarAcreditacionRechazadaCommandHandler",
        "Documentos.MarcarAcreditacionSubidaCommandHandler",
        "Documentos.RenovarDocumentoCommandHandler",
        "Documentos.ResolverRevisionIaDocumentoCommandHandler",
        "Documentos.RestaurarAnotacionAcreditacionCommandHandler",
        "Documentos.CambiarEstadoAcreditacionEnCentroCommandHandler",
        "Documentos.DeshacerCambioEstadoAcreditacionCommandHandler",
        "Documentos.RestaurarDocumentoCommandHandler",

        // Empresas
        "Empresas.BorrarCredencialAccesoEmpresaContrasenaCommandHandler",
        "Empresas.CrearEmpresaCommandHandler",
        "Empresas.EditarEmpresaCommandHandler",
        "Empresas.EditarFichaEmpresaCommandHandler",
        "Empresas.EliminarDelegacionEmpresaUOficioCommandHandler",
        "Empresas.EliminarEmpresaCommandHandler",
        "Empresas.EliminarEmpresasCommandHandler",
        "Empresas.GuardarCredencialAccesoEmpresaCommandHandler",
        "Empresas.GuardarDelegacionEmpresaCommandHandler",
        "Empresas.GuardarNotaInternaEmpresaCommandHandler",
        "Empresas.GuardarOficioEmpresaCommandHandler",
        "Empresas.RestaurarEmpresaCommandHandler",

        // Gestiones
        "Gestiones.CompletarGestionCommandHandler",
        "Gestiones.CrearGestionesParaTrabajadorCommandHandler",
        "Gestiones.EliminarGestionCommandHandler",
        "Gestiones.RestaurarGestionCommandHandler",

        // Incidencias
        "Incidencias.CrearIncidenciaCommandHandler",
        "Incidencias.EditarIncidenciaCommandHandler",
        "Incidencias.EliminarIncidenciaCommandHandler",
        "Incidencias.EliminarIncidenciasCommandHandler",
        "Incidencias.MarcarResueltaIncidenciaCommandHandler",

        // Integraciones
        "Integraciones.ConectarBuzonMicrosoft365CommandHandler",
        "Integraciones.CrearLineaWhatsAppCommandHandler",
        "Integraciones.DesconectarBuzonCommandHandler",
        "Integraciones.ReactivarConexionCommandHandler",

        // Plantillas
        "Plantillas.AgregarVersionPlantillaCommandHandler",
        "Plantillas.ConfirmarPlantillaDocumentoVersionCommandHandler",
        "Plantillas.CrearPlantillaDocumentoCommandHandler",
        "Plantillas.GenerarDocumentoIndividualCommandHandler",
        "Plantillas.GuardarElementosPlantillaCommandHandler",
        "Plantillas.IniciarLoteGeneracionDocumentosCommandHandler",

        // Proyectos
        "Proyectos.ActualizarProyectoCommandHandler",
        "Proyectos.AnadirRequisitoEventoCommandHandler",
        "Proyectos.AsignarTecnicoProyectoCommandHandler",
        "Proyectos.CerrarProyectoCommandHandler",
        "Proyectos.CrearProyectoCommandHandler",
        "Proyectos.DesasignarTecnicoProyectoCommandHandler",
        "Proyectos.EliminarProyectoCommandHandler",
        "Proyectos.GuardarPlantillaRequisitosEventoCommandHandler",
        "Proyectos.QuitarRequisitoEventoCommandHandler",
        "Proyectos.ReabrirProyectoCommandHandler",
        "Proyectos.RestaurarProyectoCommandHandler",

        // Reclamaciones
        "Reclamaciones.EnviarReclamacionCommandHandler",
        "Reclamaciones.EnviarReclamacionEmpresaCommandHandler",
        "Reclamaciones.EnviarReclamacionEmpresaEmpleadoraCommandHandler",
        "Reclamaciones.PrepararVistaPreviaReclamacionCommandHandler",

        // Subcontratas
        "Subcontratas.CambiarNivelServicioSubcontrataCommandHandler",
        "Subcontratas.CrearSubcontrataCommandHandler",
        "Subcontratas.ActualizarVerificacionExternaSubcontrataCommandHandler",
        "Subcontratas.EditarSubcontrataCommandHandler",
        "Subcontratas.EliminarSubcontrataCommandHandler",
        "Subcontratas.EliminarSubcontratasCommandHandler",
        "Subcontratas.EliminarVerificacionExternaSubcontrataCommandHandler",
        "Subcontratas.GuardarCredencialAccesoSubcontrataCommandHandler",
        "Subcontratas.GuardarNotaInternaSubcontrataCommandHandler",
        "Subcontratas.RegistrarVerificacionExternaSubcontrataCommandHandler",
        "Subcontratas.RestaurarSubcontrataCommandHandler",
        "Subcontratas.RestaurarVerificacionExternaSubcontrataCommandHandler",

        // Telemetria
        "Telemetria.RegistrarTramoGestionCommandHandler",

        // TiposDocumento
        "TiposDocumento.ActualizarLecturaIaClienteCommandHandler",
        "TiposDocumento.CrearTipoDocumentoCommandHandler",
        "TiposDocumento.EditarTipoDocumentoCommandHandler",
        "TiposDocumento.EstablecerToleranciaClienteEmpresarialCommandHandler",

        // Trabajadores
        "Trabajadores.AsignarAliasTrabajadorCommandHandler",
        "Trabajadores.CorregirDocumentoIdentidadTrabajadorCommandHandler",
        "Trabajadores.CrearTrabajadorCommandHandler",
        "Trabajadores.EditarFichaTrabajadorCommandHandler",
        "Trabajadores.EditarTrabajadorCommandHandler",
        "Trabajadores.EliminarTrabajadorCommandHandler",
        "Trabajadores.EliminarTrabajadoresCommandHandler",
        "Trabajadores.FijarDelegacionEmpresaYOficioTrabajadorCommandHandler",
        "Trabajadores.GuardarNotaInternaTrabajadorCommandHandler",
        "Trabajadores.ResolverDeteccionAusenteCommandHandler",
        "Trabajadores.ResolverDeteccionNuevoCommandHandler",
        "Trabajadores.RestaurarTrabajadorCommandHandler",

        // Vehiculos
        "Vehiculos.CrearVehiculoCommandHandler",
        "Vehiculos.EditarFichaVehiculoCommandHandler",
        "Vehiculos.EditarVehiculoCommandHandler",
        "Vehiculos.EliminarVehiculoCommandHandler",
        "Vehiculos.EliminarVehiculosCommandHandler",
        "Vehiculos.GuardarNotaInternaVehiculoCommandHandler",
        "Vehiculos.RestaurarVehiculoCommandHandler",

        // Visitas
        "Visitas.AnadirTrabajadorAVisitaCommandHandler",
        "Visitas.CancelarVisitaCommandHandler",
        "Visitas.CancelarVisitasCommandHandler",
        "Visitas.CrearVisitaCommandHandler",
        "Visitas.EditarContactoVisitaCommandHandler",
        "Visitas.EditarVisitaCommandHandler",
        "Visitas.EnviarPaqueteAcreditacionVisitaCommandHandler",
        "Visitas.MarcarDocumentacionGestionadaCommandHandler",
        "Visitas.MarcarNotificadoClienteCommandHandler",
        "Visitas.QuitarMarcaDocumentacionGestionadaCommandHandler",
        "Visitas.QuitarTrabajadorDeVisitaCommandHandler",
        "Visitas.ReactivarVisitaCommandHandler",
    ];

    /// <summary>
    /// Qué garantiza que un handler exento no necesite la cartera, verificado por reflexión o IL donde
    /// se puede. <paramref name="DelegaEn"/>: el comando de escritura que envía por <see cref="IMediator"/>
    /// (se comprueba que el handler lo construye y que el handler de ese comando llama a la cartera).
    /// <paramref name="Guardas"/>: los métodos de la autoridad que la sustituye; el <c>Handle</c> tiene
    /// que alcanzar <b>todos</b> (p. ej. resolver el contexto Y leer la propiedad que decide).
    /// <paramref name="Autoservicio"/>: la petición implementa <see cref="IComandoDeAutoservicio"/>.
    /// Una exención sin ninguna de las tres vale lo que su motivo escrito, y por eso el número de
    /// exenciones también tiene tope (<see cref="MaximoDeExentos"/>).
    /// Se nombra el método y no solo el tipo: de un <c>record</c> como <see cref="ContextoOperadorCae"/>
    /// valdría cualquier accesor o <c>Equals</c> generado, que no comprueba nada.
    /// </summary>
    private sealed record Exencion(string Motivo, Type? DelegaEn = null, Guarda[]? Guardas = null, bool Autoservicio = false);

    private sealed record Guarda(Type Tipo, string Metodo);

    private static readonly Guarda ResolverOperadorCae = new(typeof(ContextoOperadorCae), nameof(ContextoOperadorCae.ResolverAsync));

    /// <summary>Que el handler mire si la resolución falló: sin esto, ResolverAsync alcanzable no decide nada.</summary>
    private static readonly Guarda MiraSiFallo = new(
        typeof(CaeManager.Domain.Common.Result), "get_" + nameof(CaeManager.Domain.Common.Result.EsFallido));

    private static Guarda[] OperadorCae(string propiedadQueDecide) =>
        [ResolverOperadorCae, MiraSiFallo, new(typeof(ContextoOperadorCae), "get_" + propiedadQueDecide)];

    private static readonly Guarda[] SoloCoordinadorCae = OperadorCae(nameof(ContextoOperadorCae.EsCoordinadorCae));

    private static readonly Guarda[] ConRolEnOrigen = OperadorCae(nameof(ContextoOperadorCae.Rol));

    /// <summary>
    /// Solo rol Administrador, leído con <see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>: el IL
    /// prueba que el handler lee el rol, no que lo compare con «Administrador» (eso lo prueban sus tests).
    /// </summary>
    private static readonly Guarda[] SoloAdministrador =
        [new(typeof(ICurrentUserService), nameof(ICurrentUserService.ObtenerRolEfectivoAsync))];

    private static readonly Guarda[] TareaDeLaPersona =
        [new(typeof(ModificacionTareaAsistente), nameof(ModificacionTareaAsistente.AplicarAsync))];

    /// <summary>
    /// Handlers que no llaman a la comprobación, con el motivo por el que es correcto. Leídos uno a
    /// uno el 2026-10-18; el motivo dice qué fila escriben y quién decide en su lugar.
    /// </summary>
    private static readonly Dictionary<string, Exencion> Exentos = new()
    {
        // ── Tarea del asistente: la fila es de la persona, no de una cartera ────────────────────
        ["AsistenteIa.CrearTareaAsistenteCommandHandler"] = new(
            "crea una TareaAsistente de la propia persona (ResolucionPersonaTareaAsistente: Actor real y Usuario " +
            "simulado separados); no escribe ninguna fila con cartera",
            Guardas: [new(typeof(ResolucionPersonaTareaAsistente), nameof(ResolucionPersonaTareaAsistente.ResolverAsync))]),
        ["AsistenteIa.ActualizarPasoTareaAsistenteCommandHandler"] = new(
            "cambia la TareaAsistente de la propia persona, cargada con ObtenerDePersonaAsync (nunca la de otra del " +
            "mismo Tenant); no escribe ninguna fila con cartera",
            Guardas: TareaDeLaPersona),
        ["AsistenteIa.AgregarTurnoTareaAsistenteCommandHandler"] = new(
            "mismo motivo que ActualizarPasoTareaAsistenteCommandHandler", Guardas: TareaDeLaPersona),
        ["AsistenteIa.ConfirmarPlanTareaAsistenteCommandHandler"] = new(
            "mismo motivo que ActualizarPasoTareaAsistenteCommandHandler; confirmar el plan no ejecuta sus pasos",
            Guardas: TareaDeLaPersona),
        ["AsistenteIa.DescartarTareaAsistenteCommandHandler"] = new(
            "mismo motivo que ActualizarPasoTareaAsistenteCommandHandler", Guardas: TareaDeLaPersona),
        ["AsistenteIa.GuardarPlanTareaAsistenteCommandHandler"] = new(
            "mismo motivo que ActualizarPasoTareaAsistenteCommandHandler", Guardas: TareaDeLaPersona),
        ["AsistenteIa.ProponerPlanDeTareaAsistenteCommandHandler"] = new(
            "no escribe: es Command solo para revalidar la autorización de escritura antes de enviar el texto al " +
            "proveedor de IA; lee la tarea de la persona y pide el plan a ProponerPlanAsistenteQuery"),

        // ── Datos del propio usuario ─────────────────────────────────────────────────────────────
        ["BusquedaGlobal.RegistrarUsoRecienteCommandHandler"] = new(
            "autoservicio (IComandoDeAutoservicio): anota en los recientes del propio usuario lo que eligió en la " +
            "paleta; no escribe sobre la entidad nombrada", Autoservicio: true),
        ["Dashboard.GuardarPreferenciaDashboardCommandHandler"] = new(
            "autoservicio (IComandoDeAutoservicio): guarda qué KPI ve el propio usuario", Autoservicio: true),
        ["Documentos.GuardarFirmaGuardadaUsuarioCommandHandler"] = new(
            "guarda la firma habitual del propio usuario (un usuario, una firma); no toca ningún Documento"),
        ["Reportes.RegistrarHistorialInformeCommandHandler"] = new(
            "anota en el historial del propio usuario qué informe generó; no escribe sobre el Cliente empresarial, y " +
            "el informe ya sale filtrado por la cartera (mismo motivo que en EscriturasSobreEntidadesConCarteraTests)"),

        // ── Plano Comercial del Operador CAE, no filas de una cartera ───────────────────────────
        ["Dashboard.FijarTarifaOperadorCaeCommandHandler"] = new(
            "fija la tarifa que el Operador CAE externo cobra a una Empresa gestionada: una fila del Tenant de origen " +
            "del Operador, no de la Empresa gestionada; la guardan el rol efectivo en origen (Administrador o Dirección " +
            "CAE, AutorizacionTarifasOperadorCae) y una Asignación de Operación vigente del Operador sobre esa Empresa",
            Guardas:
            [
                new(typeof(CaeManager.Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae),
                    nameof(CaeManager.Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae.OperadorQueGestionaAsync)),
                new(typeof(CaeManager.Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae),
                    nameof(CaeManager.Application.Dashboard.TarifasOperadorCae.AutorizacionTarifasOperadorCae.GestionaAsync)),
            ]),

        // ── Agregado del Tenant entero, no filas de una cartera ─────────────────────────────────
        ["Dashboard.ReabrirMesCuadroDireccionCommandHandler"] = new(
            "reabre el cierre mensual del Cuadro de dirección, un agregado del Tenant beneficiario entero: solo " +
            "Dirección CAE y solo si AlcanzaElTenantEnteroAsync (acceso total o cartera de ámbito universal); una " +
            "lista de cartera no puede recortar un recuento de Tenant",
            Guardas:
            [
                new(typeof(ICurrentUserService), nameof(ICurrentUserService.ObtenerRolEfectivoAsync)),
                new(typeof(IAlcanceDatosService), nameof(IAlcanceDatosService.AlcanzaElTenantEnteroAsync)),
            ]),

        // ── Alta de una raíz nueva: no hay fila existente cuya cartera mirar ────────────────────
        ["Clientes.CrearClienteCommandHandler"] = new(
            "da de alta un Cliente empresarial nuevo; no hay fila previa en ninguna cartera y el alta no concede " +
            "alcance (D-7, 2026-10-02: el Gestor CAE queda solo como referencia, sin Asignación de Cartera)"),

        // ── Delegan por el mediador en un comando que sí comprueba ──────────────────────────────
        ["Documentos.AceptarDeteccionesIaEnBloqueCommandHandler"] = new(
            "acepta cada revisión con AplicarDeteccionIaDocumentoCommand por el pipeline completo, una transacción " +
            "por elemento; la cartera la comprueba ese handler", DelegaEn: typeof(AplicarDeteccionIaDocumentoCommand)),
        ["Documentos.ConfirmarDocumentoPropuestoPorIaCommandHandler"] = new(
            "exige una persona y delega el alta en CrearDocumentoCommand, que comprueba la cartera del Trabajador",
            DelegaEn: typeof(CrearDocumentoCommand)),
        ["Plantillas.ProcesarItemLoteGeneracionCommandHandler"] = new(
            "cada ítem del lote se genera con GenerarDocumentoIndividualCommand, que comprueba la cartera del " +
            "Trabajador o Cliente empresarial ítem a ítem", DelegaEn: typeof(GenerarDocumentoIndividualCommand)),

        // ── Configuración del Tenant, no filas de una cartera ───────────────────────────────────
        ["TiposDocumento.ActualizarDeteccionTrabajadoresGlobalCommandHandler"] = new(
            "interruptor de un Tipo de documento para todo el Tenant; solo rol Administrador, que no tiene " +
            "restricción de cartera, y excluido del encargo de administración no pleno (ActosExcluidosDelEncargo)",
            Guardas: SoloAdministrador),
        ["TiposDocumento.ActualizarLecturaIaGlobalCommandHandler"] = new(
            "mismo motivo que ActualizarDeteccionTrabajadoresGlobalCommandHandler", Guardas: SoloAdministrador),
        ["TiposDocumento.ActualizarPerfilDocumentoOficialGlobalCommandHandler"] = new(
            "mismo motivo que ActualizarDeteccionTrabajadoresGlobalCommandHandler", Guardas: SoloAdministrador),
        ["TiposDocumento.ActualizarVerificacionIaGlobalCommandHandler"] = new(
            "mismo motivo que ActualizarDeteccionTrabajadoresGlobalCommandHandler", Guardas: SoloAdministrador),
        ["Integraciones.ActualizarLineaWhatsAppCommandHandler"] = new(
            "configuración de una línea de WhatsApp del Tenant; solo rol Administrador, sin restricción de cartera",
            Guardas: SoloAdministrador),
        ["Integraciones.CambiarActivoProveedorPlataformaCommandHandler"] = new(
            "catálogo de proveedores compartido por todos los Tenants: plano de Plataforma, autorizado por " +
            "IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync, no por la cartera de un Tenant",
            Guardas: [new(typeof(IAutorizacionAdminPlataforma), nameof(IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync))]),

        // ── Gobiernan la propia Asignación de Cartera desde el Tenant de origen del Operador CAE ─
        ["Operaciones.SolicitarIncorporacionCarteraCommandHandler"] = new(
            "escribe la solicitud de incorporación a cartera: la autoridad es el rol del usuario en su Operador CAE " +
            "de origen (ContextoOperadorCae), no la cartera del Tenant activo, que es justo lo que se está pidiendo",
            Guardas: OperadorCae(nameof(ContextoOperadorCae.EsGestorCae))),
        ["Operaciones.AceptarSolicitudIncorporacionCarteraCommandHandler"] = new(
            "mismo motivo que SolicitarIncorporacionCarteraCommandHandler: concede la Asignación de Cartera; solo el " +
            "Coordinador CAE", Guardas: SoloCoordinadorCae),
        ["Operaciones.RechazarSolicitudIncorporacionCarteraCommandHandler"] = new(
            "mismo motivo que SolicitarIncorporacionCarteraCommandHandler; solo el Coordinador CAE",
            Guardas: SoloCoordinadorCae),
        ["Operaciones.RevocarIncorporacionCarteraCommandHandler"] = new(
            "mismo motivo que SolicitarIncorporacionCarteraCommandHandler: retira la Asignación de Cartera; el " +
            "Coordinador CAE o quien la pidió", Guardas: SoloCoordinadorCae),
        ["Operaciones.ProponerApoyoCarteraCommandHandler"] = new(
            "propuesta de apoyo de cartera entre Gestores CAE del mismo Operador CAE: la autoridad es el rol en el " +
            "Tenant de origen (ContextoOperadorCae)",
            Guardas: OperadorCae(nameof(ContextoOperadorCae.ParticipaEnIncorporacionCartera))),
        ["Operaciones.AceptarPropuestaApoyoCarteraCommandHandler"] = new(
            "mismo motivo que ProponerApoyoCarteraCommandHandler; exige además que el actor sea el destinatario",
            Guardas: ConRolEnOrigen),
        ["Operaciones.RechazarPropuestaApoyoCarteraCommandHandler"] = new(
            "mismo motivo que ProponerApoyoCarteraCommandHandler", Guardas: ConRolEnOrigen),
        ["Operaciones.RetirarPropuestaApoyoCarteraCommandHandler"] = new(
            "mismo motivo que ProponerApoyoCarteraCommandHandler", Guardas: ConRolEnOrigen),
        ["Operaciones.TerminarApoyoCarteraCommandHandler[DesasignarmeDeApoyoCommand]"] = new(
            "fin de un apoyo de cartera: autoridad del Tenant de origen (ContextoOperadorCae o " +
            "AutoridadSobreCarteraDeGestorCae, con el rol leído en Identity)", Guardas: ConRolEnOrigen),
        ["Operaciones.TerminarApoyoCarteraCommandHandler[RetirarApoyoConcedidoCommand]"] = new(
            "mismo motivo que DesasignarmeDeApoyoCommand", Guardas: ConRolEnOrigen),
        ["Operaciones.TerminarApoyoCarteraCommandHandler[RevocarApoyoCarteraCommand]"] = new(
            "mismo motivo que DesasignarmeDeApoyoCommand", Guardas: ConRolEnOrigen),
    };

    /// <summary>
    /// Tope de <see cref="Exentos"/>: una exención nueva tiene que subir este número en el diff, para que
    /// la revisión la vea. Si bajan, baja también.
    /// </summary>
    private const int MaximoDeExentos = 34;

    /// <summary>De <see cref="Exentos"/>, los que no tienen nada verificable: solo su motivo escrito.</summary>
    private const int MaximoDeExentosSoloConMotivo = 4;

    /// <summary>
    /// Carpetas de Application con comandos de escritura que quedan fuera de la población porque ningún
    /// handler suyo inyecta <see cref="IAlcanceDatosService"/>. Se congelan para que una carpeta nueva
    /// sea una decisión consciente y no un hueco silencioso de la derivación.
    /// </summary>
    private static readonly Dictionary<string, string> CaracteristicasSinCartera = new()
    {
        ["ApiKeys"] =
            "claves de API de una delegación de soporte: plano de Plataforma, solo desde el Tenant de la consultora " +
            "delegada; no escribe filas de una cartera",
        ["Comercial"] =
            "estado comercial y suscripción del Tenant: plano Comercial, autorizado por " +
            "IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync",
        ["Configuracion"] =
            "parámetros, automatizaciones y presupuesto de IA del Tenant (por rol) y preferencias del propio usuario " +
            "(filtros, vistas recordadas, orden de cajas); ninguna fila con cartera",
        ["Cumplimiento"] =
            "aceptación de términos de la propia persona (autoservicio) e instrucción de tratamiento de IA del " +
            "Tenant propietario: plano de Propiedad, autorizado por IAutorizacionAdminPlataforma o rol Administrador",
        ["Facturacion"] =
            "tarifas del Cliente empresarial: plano Comercial, solo rol Administrador (AutorizacionTarifasCliente); " +
            "su red está en EscriturasSobreEntidadesConCarteraTests",
        ["Importacion"] =
            "alta masiva solo para Administrador, que no tiene restricción de cartera; su red está en " +
            "EscriturasSobreEntidadesConCarteraTests (ComandosDeImportacion)",
        ["Notificaciones"] =
            "marca leída una notificación de la propia persona (IComandoDeAutoservicio)",
        ["Plataforma"] =
            "Sesión Privilegiada y concesiones del Actor de Plataforma TALVEG: plano de Plataforma, no de Operación",
        ["Retencion"] =
            "solicitudes de purga por retención de datos: proceso de Plataforma sobre el Tenant entero, no una cartera",
        ["Tenants"] =
            "delegaciones, Asignaciones de Operación, encargo de administración y logo del Tenant: gobiernan los " +
            "planos de Propiedad y Operación entre Tenants, con su autoridad propia (AutoridadSobreElEncargo…), no " +
            "filas dentro de una cartera",
        ["Usuarios"] =
            "cuentas, roles y la propia Asignación de Cartera (AsignarCarteraGestorCae, DesignarGestorCaePrincipal, " +
            "EditarUsuario…): la autoridad es el rol del actor sobre la cuenta (AutoridadSobreCuentas, " +
            "AutoridadSobreCarteraDeGestorCae), no la cartera que se está gobernando",
        ["VigilanciaNormativa"] =
            "marca revisado un aviso de revisión normativa: plano de Plataforma, autorizado por " +
            "IAutorizacionAdminPlataforma.PuedeGlobalmenteAsync",
    };

    /// <summary>Referencias del IL de Web que no se resuelven fuera del host (componentes de Blazor).</summary>
    private const int MaximoDeIrresolublesEnWeb = 0;

    /// <summary>
    /// Deuda conocida: handlers que <b>deberían</b> comprobar la cartera y hoy no lo hacen. No se
    /// corrigen aquí (cada uno es un incremento con su test de Application); solo se congelan para
    /// que no crezcan. Una entrada que ya llama hace fallar el test para que pase a <see cref="Llaman"/>.
    /// Hoy está vacía: los seis que nacieron aquí (los dos descartes de sugerencias de correo y las cuatro
    /// escrituras de Plantillas) se corrigieron en #1424, #1425 y #1430 y están en <see cref="Llaman"/>.
    /// </summary>
    private static readonly Dictionary<string, string> PendientesDeCorregir = new();

    /// <summary>
    /// Tope de <see cref="PendientesDeCorregir"/>: solo baja. Subirlo exige que el diff lo enseñe.
    /// </summary>
    private const int MaximoDePendientes = 0;

    private static readonly Lazy<Medida> MedidaActual = new(Medir);

    [Fact]
    public void Las_caracteristicas_con_cartera_salen_de_quien_inyecta_el_alcance()
    {
        var medida = MedidaActual.Value;

        // Control positivo de la población: si la reflexión dejara de ver los handlers que inyectan el
        // alcance, la población quedaría vacía y todo lo demás pasaría comparando vacíos.
        medida.Caracteristicas.Should().Contain(
            new[] { "Centros", "Clientes", "Documentos", "Empresas", "Gestiones", "Subcontratas", "Trabajadores", "Vehiculos" },
            "son las carpetas cuyo agregado tiene cartera definida en IAlcanceDatosService");
        medida.Handlers.Should().HaveCountGreaterThan(100,
            "hoy hay 222 comandos de escritura en las características con cartera; muy pocos significa que la medida ya no los ve");
    }

    [Fact]
    public void Todo_comando_de_escritura_con_cartera_esta_en_exactamente_una_lista()
    {
        var medida = MedidaActual.Value;
        var listas = new (string Nombre, IEnumerable<string> Claves)[]
        {
            (nameof(Llaman), Llaman), (nameof(Exentos), Exentos.Keys), (nameof(PendientesDeCorregir), PendientesDeCorregir.Keys),
        };

        var repetidas = listas.SelectMany(l => l.Claves.Select(c => (l.Nombre, Clave: c)))
            .GroupBy(x => x.Clave)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} en {string.Join(" y ", g.Select(x => x.Nombre))}");
        string.Join("\n", repetidas).Should().BeEmpty("cada handler va en una sola lista");

        var clasificadas = listas.SelectMany(l => l.Claves).ToHashSet(StringComparer.Ordinal);

        var sinClasificar = medida.Handlers
            .Where(h => !clasificadas.Contains(h.Key))
            .OrderBy(h => h.Key, StringComparer.Ordinal)
            .Select(h => h.Value.Llama
                ? $"{h.Key}: llama ({string.Join(" > ", h.Value.Camino)}) -> añádelo a Llaman"
                : $"{h.Key}: NO llama a la comprobación de cartera -> añade la llamada antes de escribir " +
                  "(p. ej. EmpresaParaGestionVisibleAsync) o, si de verdad no aplica, a Exentos con el motivo");
        string.Join("\n", sinClasificar).Should().BeEmpty(
            "todo comando de escritura de una característica con cartera tiene que clasificarse; un handler que " +
            "inyecta IAlcanceDatosService sin llamarlo es el defecto que esta red existe para parar");

        var obsoletas = clasificadas.Where(c => !medida.Handlers.ContainsKey(c)).Order(StringComparer.Ordinal);
        string.Join("\n", obsoletas).Should().BeEmpty(
            "estas entradas ya no corresponden a ningún comando de escritura de una característica con cartera: retíralas");
    }

    [Fact]
    public void Los_de_Llaman_llaman_y_los_demas_no()
    {
        var medida = MedidaActual.Value;

        var dejaronDeLlamar = Llaman.Where(c => medida.Handlers.TryGetValue(c, out var h) && !h.Llama).Order(StringComparer.Ordinal);
        string.Join("\n", dejaronDeLlamar).Should().BeEmpty(
            "estos handlers estaban en Llaman y su Handle ya no alcanza ningún método de cartera de IAlcanceDatosService: " +
            "o se perdió la comprobación (restáurala) o se movió a un sitio que el grafo de IL no ve");

        var yaLlaman = Exentos.Keys.Concat(PendientesDeCorregir.Keys)
            .Where(c => medida.Handlers.TryGetValue(c, out var h) && h.Llama)
            .Order(StringComparer.Ordinal)
            .Select(c => $"{c} ({string.Join(" > ", medida.Handlers[c].Camino)})");
        string.Join("\n", yaLlaman).Should().BeEmpty(
            "estos handlers ya llaman a la comprobación: pásalos a Llaman para que no puedan volver a perderla");
    }

    [Fact]
    public void Las_exenciones_verificables_se_sostienen()
    {
        var medida = MedidaActual.Value;
        var fallos = new List<string>();

        foreach (var (clave, exencion) in Exentos.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(exencion.Motivo))
                fallos.Add($"{clave}: sin motivo");

            if (!medida.Handlers.TryGetValue(clave, out var handler))
                continue; // la obsolescencia la señala Todo_comando_de_escritura_con_cartera_esta_en_exactamente_una_lista

            if (exencion.DelegaEn is { } comando)
            {
                if (!medida.Alcanza(handler.Handle, comando, metodo: null))
                    fallos.Add($"{clave}: dice delegar en {comando.Name} y su Handle ya no lo construye");
                if (!medida.LlamaElHandlerDe.TryGetValue(comando, out var llama))
                    fallos.Add($"{clave}: {comando.Name} no tiene handler de escritura");
                else if (!llama)
                    fallos.Add($"{clave}: delega en {comando.Name}, cuyo handler NO llama a la comprobación de cartera");
            }

            foreach (var guarda in exencion.Guardas ?? [])
            {
                if (!medida.Alcanza(handler.Handle, guarda.Tipo, guarda.Metodo))
                    fallos.Add($"{clave}: dice apoyarse en {guarda.Tipo.Name}.{guarda.Metodo} y su Handle ya no llega a él");
            }

            if (exencion.Autoservicio && !typeof(IComandoDeAutoservicio).IsAssignableFrom(handler.Peticion))
                fallos.Add($"{clave}: dice ser autoservicio y {handler.Peticion.Name} ya no implementa IComandoDeAutoservicio");
        }

        string.Join("\n", fallos).Should().BeEmpty(
            "una exención vale lo que su garantía: si el comando delegado o la autoridad propia dejan de estar en el " +
            "camino, el handler escribe sin ninguna comprobación");
    }

    [Fact]
    public void Las_exenciones_solo_crecen_a_la_vista()
    {
        Exentos.Should().HaveCount(MaximoDeExentos,
            "una exención nueva sube MaximoDeExentos en el mismo diff, para que la revisión la vea; si bajaron, " +
            "baja también el tope");

        var soloConMotivo = Exentos.Count(e => e.Value is { DelegaEn: null, Guardas: null, Autoservicio: false });
        soloConMotivo.Should().BeLessThanOrEqualTo(MaximoDeExentosSoloConMotivo,
            "las exenciones que solo se sostienen en su motivo escrito no crecen: si se puede, que digan qué guarda " +
            "verificable las sustituye");
    }

    [Fact]
    public void Las_carpetas_con_escrituras_y_sin_cartera_estan_declaradas()
    {
        var medida = MedidaActual.Value;

        var nuevas = medida.CaracteristicasConEscritura
            .Except(medida.Caracteristicas)
            .Where(c => !CaracteristicasSinCartera.ContainsKey(c))
            .Order(StringComparer.Ordinal);
        string.Join("\n", nuevas).Should().BeEmpty(
            "estas carpetas tienen comandos de escritura y ningún handler que inyecte IAlcanceDatosService, así que " +
            "esta red no las mira: añádelas a CaracteristicasSinCartera con el motivo, o inyecta y comprueba la cartera");

        var obsoletas = CaracteristicasSinCartera.Keys
            .Where(c => medida.Caracteristicas.Contains(c) || !medida.CaracteristicasConEscritura.Contains(c))
            .Order(StringComparer.Ordinal);
        string.Join("\n", obsoletas).Should().BeEmpty(
            "estas carpetas ya inyectan el alcance (y entran en la población) o ya no tienen escrituras: retíralas");

        CaracteristicasSinCartera.Values.Should().NotContain(v => string.IsNullOrWhiteSpace(v), "cada exclusión dice por qué");
    }

    [Fact]
    public void El_grafo_no_pierde_aristas_en_Application_ni_Infrastructure()
    {
        var medida = MedidaActual.Value;

        string.Join("\n", medida.IrresolublesFueraDeWeb).Should().BeEmpty(
            "una referencia que no se resuelve es una arista perdida: puede esconder una llamada o, por una interfaz, " +
            "una implementación que no llama");
        medida.IrresolublesEnWeb.Should().BeLessThanOrEqualTo(MaximoDeIrresolublesEnWeb,
            "las de Web son referencias a componentes que no cargan fuera del host; si crecen, el grafo ve menos de Web");
    }

    [Fact]
    public void Los_pendientes_de_corregir_solo_encogen()
    {
        PendientesDeCorregir.Should().HaveCountLessThanOrEqualTo(MaximoDePendientes,
            "PendientesDeCorregir es deuda: una entrada nueva no se añade aquí, se corrige el handler");
        PendientesDeCorregir.Values.Should().NotContain(v => string.IsNullOrWhiteSpace(v), "cada pendiente dice qué le falta");
        PendientesDeCorregir.Should().HaveCount(MaximoDePendientes,
            "si la deuda bajó, baja también MaximoDePendientes en el mismo commit para que no pueda volver a subir");
    }

    // ── Control positivo del instrumento ────────────────────────────────────────────────────────

    [Fact]
    public void El_grafo_de_IL_distingue_llamar_de_depender()
    {
        var grafo = new GrafoDeLlamadasIl([typeof(LlamadaAlAlcanceDeCarteraEnEscrituraTests).Assembly, typeof(ICommand).Assembly]);
        var alcanzan = grafo.QueAlcanzan(MetodosQueComprueban());

        bool Llama(Type tipo) => alcanzan.Contains(ClaveMetodo.De(tipo.GetMethod("Handle")!));

        Llama(typeof(ControlLlamaPorExtension)).Should().BeTrue("una extensión de alcance dentro de un método async es una llamada");
        Llama(typeof(ControlLlamaEnLambda)).Should().BeTrue("una lambda pasada como delegado (transacción con callback) es una llamada");
        Llama(typeof(ControlLlamaPorGenerico)).Should().BeTrue("un auxiliar genérico instanciado es una llamada");
        Llama(typeof(ControlLlamaPorServicio)).Should().BeTrue("un servicio cuyas implementaciones llaman todas es una llamada");

        Llama(typeof(ControlSoloDepende)).Should().BeFalse("inyectar el servicio y guardarlo en un campo no es llamarlo");
        Llama(typeof(ControlSoloLadoDeGestion)).Should().BeFalse(
            "OperaDesdeElLadoDeGestionAsync distingue al rol Cliente y no es una puerta de cartera");
        Llama(typeof(ControlServicioParcial)).Should().BeFalse(
            "un servicio con una implementación que no llama no garantiza la comprobación");
        Llama(typeof(ControlServicioSinImplementacion)).Should().BeFalse(
            "una interfaz que nadie implementa en los ensamblados analizados no garantiza nada (All sobre vacío)");
        Llama(typeof(ControlSoloAccesoTotal)).Should().BeFalse(
            "TieneAccesoTotalAsync solo dice si el rol ve todo; no decide sobre ninguna fila de la cartera");
    }

    // Tipos sintéticos del control positivo: nunca entradas de las listas de arriba.
    internal sealed class ControlLlamaPorExtension(IAlcanceDatosService alcance)
    {
        public async Task<bool> Handle(Guid empresaId) => await alcance.EmpresaParaGestionVisibleAsync(empresaId);
    }

    internal sealed class ControlLlamaEnLambda(IAlcanceDatosService alcance)
    {
        public Task<bool> Handle(Func<Func<Task<bool>>, Task<bool>> enTransaccion) =>
            enTransaccion(async () => await alcance.ObtenerTrabajadorIdsVisiblesAsync() is null);
    }

    internal sealed class ControlLlamaPorGenerico(IAlcanceDatosService alcance)
    {
        public Task<bool> Handle(Guid id) => Auxiliar.Comprobar(alcance, id);
    }

    internal static class Auxiliar
    {
        public static async Task<bool> Comprobar<T>(IAlcanceDatosService alcance, T _) =>
            await alcance.ObtenerClienteIdsVisiblesAsync() is null;
    }

    internal interface IComprobadorDeControl
    {
        Task<bool> ComprobarAsync();
    }

    internal sealed class ComprobadorA(IAlcanceDatosService alcance) : IComprobadorDeControl
    {
        public async Task<bool> ComprobarAsync() => await alcance.ObtenerEmpresaIdsParaGestionAsync() is null;
    }

    internal sealed class ComprobadorB(IAlcanceDatosService alcance) : IComprobadorDeControl
    {
        public async Task<bool> ComprobarAsync() => await alcance.ObtenerCentroIdsVisiblesAsync() is null;
    }

    internal sealed class ControlLlamaPorServicio(IComprobadorDeControl comprobador)
    {
        public Task<bool> Handle() => comprobador.ComprobarAsync();
    }

    internal sealed class ControlSoloDepende(IAlcanceDatosService alcance)
    {
        private readonly IAlcanceDatosService _alcance = alcance;

        public Task<bool> Handle() => Task.FromResult(_alcance is not null);
    }

    internal sealed class ControlSoloLadoDeGestion(IAlcanceDatosService alcance)
    {
        public Task<bool> Handle() => alcance.OperaDesdeElLadoDeGestionAsync();
    }

    internal interface IComprobadorParcial
    {
        Task<bool> ComprobarAsync();
    }

    internal sealed class ComprobadorParcialQueLlama(IAlcanceDatosService alcance) : IComprobadorParcial
    {
        public async Task<bool> ComprobarAsync() => await alcance.ObtenerVehiculoIdsVisiblesAsync() is null;
    }

    internal sealed class ComprobadorParcialQueNoLlama : IComprobadorParcial
    {
        public Task<bool> ComprobarAsync() => Task.FromResult(true);
    }

    internal sealed class ControlServicioParcial(IComprobadorParcial comprobador)
    {
        public Task<bool> Handle() => comprobador.ComprobarAsync();
    }

    internal interface IComprobadorSinImplementacion
    {
        Task<bool> ComprobarAsync();
    }

    internal sealed class ControlServicioSinImplementacion(IComprobadorSinImplementacion comprobador)
    {
        public Task<bool> Handle() => comprobador.ComprobarAsync();
    }

    internal sealed class ControlSoloAccesoTotal(IAlcanceDatosService alcance)
    {
        public async Task<bool> Handle() => await alcance.TieneAccesoTotalAsync() && await alcance.AlcanzaElTenantEnteroAsync();
    }

    // ── Medida ──────────────────────────────────────────────────────────────────────────────────

    private sealed record HandlerMedido(MethodInfo Handle, Type Peticion, bool Llama, IReadOnlyList<string> Camino);

    private sealed class Medida(
        GrafoDeLlamadasIl grafo,
        HashSet<string> caracteristicas,
        HashSet<string> caracteristicasConEscritura,
        Dictionary<string, HandlerMedido> handlers,
        Dictionary<Type, bool> llamaElHandlerDe)
    {
        public HashSet<string> Caracteristicas { get; } = caracteristicas;
        public HashSet<string> CaracteristicasConEscritura { get; } = caracteristicasConEscritura;
        public Dictionary<string, HandlerMedido> Handlers { get; } = handlers;
        public Dictionary<Type, bool> LlamaElHandlerDe { get; } = llamaElHandlerDe;

        private static readonly Module ModuloWeb = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web").ManifestModule;

        public IEnumerable<string> IrresolublesFueraDeWeb =>
            grafo.Irresolubles.Where(i => i.Modulo != ModuloWeb).Select(i => i.Detalle);

        public int IrresolublesEnWeb => grafo.Irresolubles.Count(i => i.Modulo == ModuloWeb);

        private readonly Dictionary<(Type, string?), HashSet<ClaveMetodo>> _alcanzanA = new();

        /// <summary>
        /// ¿Alcanza <paramref name="desde"/> un constructor de <paramref name="tipo"/> (sin
        /// <paramref name="metodo"/>) o un método suyo con ese nombre?
        /// </summary>
        public bool Alcanza(MethodBase desde, Type tipo, string? metodo)
        {
            if (!_alcanzanA.TryGetValue((tipo, metodo), out var alcanzan))
            {
                const BindingFlags Todos = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                                           | BindingFlags.Static | BindingFlags.DeclaredOnly;
                MethodBase[] objetivos = metodo is null
                    ? tipo.GetConstructors(Todos)
                    : tipo.GetMethods(Todos).Where(m => m.Name == metodo).ToArray();
                objetivos.Should().NotBeEmpty($"{tipo.Name} tiene que declarar {metodo ?? "un constructor"}");
                _alcanzanA[(tipo, metodo)] = alcanzan = grafo.QueAlcanzan(objetivos);
            }

            return alcanzan.Contains(ClaveMetodo.De(desde));
        }
    }

    private static Medida Medir()
    {
        var application = typeof(ICommand).Assembly;
        var grafo = new GrafoDeLlamadasIl(
        [
            application,
            typeof(CaeManager.Infrastructure.Persistence.CaeManagerDbContext).Assembly,
            ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web"),
        ]);
        var objetivos = MetodosQueComprueban();
        var alcanzan = grafo.QueAlcanzan(objetivos);

        var handlers = ReflexionArquitecturaHelper.TiposDe(application)
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .SelectMany(t => t.GetInterfaces()
                .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IRequestHandler<,>))
                .Select(i => (Handler: t, Interfaz: i, Peticion: i.GetGenericArguments()[0])))
            .ToList();

        var caracteristicas = handlers
            .Where(h => Parametros(h.Handler).Contains(typeof(IAlcanceDatosService)))
            .Select(h => Caracteristica(h.Handler))
            .ToHashSet(StringComparer.Ordinal);

        var escrituras = handlers.Where(h => typeof(ICommandBase).IsAssignableFrom(h.Peticion)).ToList();
        var caracteristicasConEscritura = escrituras.Select(h => Caracteristica(h.Handler)).ToHashSet(StringComparer.Ordinal);

        var llamaElHandlerDe = new Dictionary<Type, bool>();
        var medidos = new Dictionary<string, HandlerMedido>(StringComparer.Ordinal);
        foreach (var grupo in escrituras.GroupBy(h => h.Handler))
        {
            var varios = grupo.Count() > 1;
            foreach (var (handler, interfaz, peticion) in grupo)
            {
                var handle = (MethodInfo)handler.GetInterfaceMap(interfaz).TargetMethods
                    .Single(m => m.Name.EndsWith("Handle", StringComparison.Ordinal));
                var llama = alcanzan.Contains(ClaveMetodo.De(handle));
                llamaElHandlerDe[peticion] = llama;

                if (!caracteristicas.Contains(Caracteristica(handler)))
                    continue;

                var clave = $"{Caracteristica(handler)}.{handler.Name}" + (varios ? $"[{peticion.Name}]" : string.Empty);
                if (!medidos.TryAdd(clave, new HandlerMedido(handle, peticion, llama, grafo.Camino(handle, objetivos, alcanzan))))
                    throw new InvalidOperationException(
                        $"Dos handlers de escritura se llaman {clave}: la clave de las listas dejaría de ser única");
            }
        }

        return new Medida(grafo, caracteristicas, caracteristicasConEscritura, medidos, llamaElHandlerDe);
    }

    /// <summary>
    /// Los métodos de <see cref="IAlcanceDatosService"/> que devuelven qué filas de la cartera alcanza
    /// quien escribe: las listas <c>Obtener…IdsVisiblesAsync</c> / <c>Obtener…IdsParaGestionAsync</c> y
    /// <see cref="IAlcanceDatosService.SubcontrataEliminadaParaGestionVisibleAsync"/>. Las extensiones por
    /// entidad (<c>…VisibleAsync</c>, <c>…ParaGestionVisibleAsync</c>) cuentan porque llaman a una lista.
    ///
    /// <para>
    /// Fuera: <see cref="IAlcanceDatosService.TieneAccesoTotalAsync"/> y
    /// <see cref="IAlcanceDatosService.AlcanzaElTenantEnteroAsync"/> (dicen si el rol lo ve todo, no si
    /// una fila concreta está en la cartera), <see cref="IAlcanceDatosService.OperaDesdeElLadoDeGestionAsync"/>
    /// (distingue al rol Cliente y no deniega nada por sí solo) y
    /// <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/> (propiedad de un buzón personal,
    /// otro eje).
    /// </para>
    ///
    /// <para>
    /// <b>Límite conocido:</b> pedir una lista cuenta como «llama» aunque el handler no la cruce con la
    /// fila que escribe; el IL no ve el uso del valor devuelto. Separar «pide la lista» de «decide con
    /// ella» exigiría análisis de flujo de datos.
    /// </para>
    /// </summary>
    private static IReadOnlyCollection<MethodBase> MetodosQueComprueban() =>
        typeof(IAlcanceDatosService).GetMethods()
            .Where(m => m.Name is not (nameof(IAlcanceDatosService.OperaDesdeElLadoDeGestionAsync)
                                    or nameof(IAlcanceDatosService.ConexionIntegracionVisibleAsync)
                                    or nameof(IAlcanceDatosService.TieneAccesoTotalAsync)
                                    or nameof(IAlcanceDatosService.AlcanzaElTenantEnteroAsync)))
            .ToList();

    private static string Caracteristica(Type handler) =>
        handler.Namespace?.Split('.') is [_, "Application", var area, ..] ? area : "(raíz)";

    private static HashSet<Type> Parametros(Type tipo) =>
        tipo.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType).ToHashSet();
}
