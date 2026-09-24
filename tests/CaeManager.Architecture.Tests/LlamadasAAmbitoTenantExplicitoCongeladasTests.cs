using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Qué ficheros pueden establecer un tenant explícito fuera de sesión, y cuáles no.</b>
///
/// <para>
/// <c>AmbitoTenantExplicito.Establecer(Guid)</c> es un método público que acepta
/// cualquier <c>Guid</c> y cambia el tenant tanto para el filtro global de EF como
/// para RLS (<c>TenantRlsConnectionInterceptor</c> lee <c>TenantIdActual</c> para el
/// <c>SET app.tenant_id</c>). Es <b>autoridad ambiental separada del control de
/// autorización</b>: el método en sí no comprueba nada — confía en que quien lo llama
/// ya decidió, por otra vía, que ese tenant es el correcto. Auditoría del Módulo 1,
/// hallazgo 🟠 #3 (2026-08-30), sin vulnerabilidad demostrada: hoy los 26 sitios que
/// lo llaman pasan un <c>Guid</c> que viene de una fuente ya verificada — una entidad
/// cargada por su Id autorizado, una credencial comprobada, o la propia enumeración
/// interna de un seeder o de un job de fondo — nunca de un parámetro externo sin
/// validar. Este ratchet no cambia esa realidad: la fija, para que un sitio nuevo que
/// pase un Guid sin verificar tenga que declararlo aquí y no colarse en silencio.
/// </para>
///
/// <para>
/// <b>Por qué a nivel de fichero y no de línea.</b> A diferencia de
/// <see cref="UsosDeEsPlataformaCongeladosTests"/> (que cuenta apariciones exactas
/// porque ahí importa el número), aquí la pregunta es binaria por sitio de llamada:
/// ¿qué construye el <c>Guid</c> que se pasa, un dato ya autorizado o algo que no lo
/// está? Eso se lee una vez por fichero al añadir la entrada; contar líneas no
/// aportaría nada que el motivo escrito no diga ya, y con 45 llamadas en 26 ficheros
/// —varios de ellos con 2 o más, como <c>DelegacionDemoSeeder</c> con 10— sí añadiría
/// churn: cualquier <c>using</c> nuevo dentro de un fichero ya categorizado pondría
/// esto en rojo sin que la propiedad vigilada hubiera cambiado.
/// </para>
///
/// <para>
/// <b>Lo que este ratchet NO demuestra.</b> (1) Que el <c>Guid</c> de cada llamada sea
/// correcto en tiempo de ejecución — congela <i>de dónde viene sintácticamente</i>
/// (el nombre de la variable/expresión, leído en la categorización), no que esa
/// variable contenga siempre el tenant debido; eso lo prueban los tests de
/// integración de cada consumidor, no este. (2) Que la categoría asignada sea
/// verificada por el instrumento — es lectura humana, igual que en
/// <see cref="FronteraDeSeedersDeBootstrapTests"/>. (3) Nada sobre <c>tests/</c>, que
/// queda fuera a propósito: un test que abra el ámbito para sembrar datos de prueba
/// no es el patrón de producción que esto vigila.
/// </para>
/// </summary>
public class LlamadasAAmbitoTenantExplicitoCongeladasTests
{
    private enum Categoria
    {
        /// <summary>
        /// El comando/consulta de Application ya cargó y validó una entidad
        /// (Delegación, Cliente) por su Id autorizado antes de establecer el
        /// ámbito con el TenantId que esa entidad trae.
        /// </summary>
        DelegacionOClienteYaValidado,

        /// <summary>
        /// Job de fondo (HostedService) que recorre tenants uno a uno desde su
        /// propia enumeración interna — el TenantId no llega de ninguna petición
        /// externa a ese bucle.
        /// </summary>
        JobDeFondoSobreEnumeracionPropia,

        /// <summary>
        /// El TenantId sale de verificar una credencial externa (clave de API,
        /// firma/clientState de webhook) inmediatamente antes de la llamada.
        /// </summary>
        CredencialVerificadaInmediatamenteAntes,

        /// <summary>Siembra o arranque: solo corre en bootstrap, sin sesión de usuario.</summary>
        BootstrapOSiembra,

        /// <summary>
        /// Servicio de la plataforma con su propia comprobación de autoridad ya
        /// hecha antes de esta línea (allowlist, sesión de soporte ya abierta).
        /// </summary>
        ServicioDePlataformaConGuardaPropia,

        /// <summary>
        /// El TenantId es el objetivo explícito de una operación administrativa
        /// de plataforma, verificada contra el modelo de concesiones de ADR-011
        /// § 4bis (<c>IAutorizacionAdminPlataforma.PuedeSobreTenantAsync</c>) —
        /// no una Delegación ni un Cliente cargado, sino la capacidad
        /// AdminPlataforma sobre ESE tenant concreto, comprobada antes de la
        /// línea que abre el ámbito.
        /// </summary>
        AdminPlataformaVerificadoPorConcesion,

        /// <summary>
        /// El TenantId es el tenant de ORIGEN del usuario de la sesión
        /// (<c>ICurrentUserService.ObtenerTenantOrigenIdAsync</c>, que sale del
        /// claim de su cuenta, no de la petición ni del workspace que tenga
        /// abierto). Abrir el ámbito sobre la propia organización no cruza
        /// ninguna frontera: devuelve al usuario a donde RLS ya le deja estar,
        /// para leer allí su rol de sesión y escribir lo que es de su Operador CAE.
        /// </summary>
        TenantDeOrigenDelUsuario,
    }

    private sealed record EntradaBlanca(Categoria Categoria, string Motivo);

    /// <summary>
    /// Medido el 2026-08-31 sobre <c>origin/main</c>: 26 ficheros, 45 llamadas.
    /// Cada entrada se leyó en su sitio — el <c>Guid</c> pasado a <c>Establecer</c> y
    /// de dónde sale — antes de asignarle categoría, no al revés.
    /// Actualizado 2026-09-02 (salud de plataforma, A-06/A-07): <b>26 ficheros, 47
    /// llamadas</b> — <c>IngestaWebhookWhatsAppHostedService.cs</c> pasa de 2 a 4 al
    /// cablear el mismo interruptor de <c>CatalogoAutomatizaciones</c> y el mismo
    /// registro de última ejecución que ya tenía su mellizo M365, siempre sobre el
    /// <c>tenantId</c> de la enumeración propia del job.
    /// Actualizado 2026-09-03 (HO-084-01, REC-084): <b>27 ficheros, 50 llamadas</b> —
    /// nuevo <c>RetencionHostedService.cs</c> (3 llamadas), mismo patrón que
    /// <c>VigilanciaVisitasUrgentesHostedService.cs</c>: barrido diario sobre la
    /// enumeración propia de tenants activos.
    /// Actualizado 2026-09-04 (HO-035-02, REC-035): <b>30 ficheros, 54 llamadas</b> —
    /// tres ficheros nuevos de Application (Registrar/Revocar/ObtenerHistorico
    /// de <c>InstruccionTratamientoIaTenantPropietario</c>, categoría nueva
    /// <see cref="Categoria.AdminPlataformaVerificadoPorConcesion"/>) y una
    /// llamada más en <c>DatosPruebaSeeder.cs</c> (de 1 a 2): siembra la
    /// instrucción de Nivel 0 solo para el tenant #1, mismo patrón
    /// <see cref="Categoria.BootstrapOSiembra"/> que ya tenía.
    ///
    /// <para>
    /// Actualizado 2026-09-22 (solicitud de incorporación a cartera): tres
    /// ficheros nuevos de Application. <c>ContextoOperadorCae.cs</c> (2 llamadas,
    /// categoría nueva <see cref="Categoria.TenantDeOrigenDelUsuario"/>) y
    /// Aceptar/Revocar (1 cada uno, sobre el Tenant propietario de una solicitud
    /// ya cargada por el Operador CAE de origen).
    /// </para>
    ///
    /// <para>
    /// Actualizado 2026-09-23 (P11, marcador de elegibilidad de Operador CAE
    /// externo): <c>DelegacionDemoSeeder.cs</c> pasa de 9 a 10 llamadas — el
    /// reaprovisionamiento de un tenant existente que aún no tenía la
    /// capacidad ahora la concede en su propio guardado (<c>Establecer</c>
    /// sobre el mismo <c>tenantExistente.Id</c> que el alta nueva ya usaba),
    /// mismo patrón <see cref="Categoria.BootstrapOSiembra"/> que ya tenía.
    /// </para>
    ///
    /// <para>
    /// Actualizado 2026-09-18 (REC-212, auditoría de los 42 usos fuera de
    /// Application frente al patrón de envenenamiento de REC-195): esta
    /// tabla ya congelaba la PROCEDENCIA del <c>Guid</c> de cada sitio, pero
    /// no decía nada sobre si REUTILIZAR la misma instancia de scope/DbContext
    /// para varios tenants sin <c>CreateScope()</c> entre vueltas podía
    /// envenenar algo — la pregunta de REC-195, no la de este ratchet. Medido
    /// uno a uno sobre <c>origin/main</c> <c>0ec6c37b</c>:
    /// </para>
    /// <para>
    /// Actualizado 2026-09-23: <c>ExpiracionAsignacionesHostedService.cs</c> entra en
    /// <see cref="Categoria.JobDeFondoSobreEnumeracionPropia"/> (1 llamada), con un
    /// <c>CreateScope()</c> por Tenant — el recuento de la lista de abajo es el del
    /// 2026-09-18 y no lo incluye.
    /// </para>
    /// <list type="bullet">
    /// <item><description>Los 9 ficheros de <see cref="Categoria.JobDeFondoSobreEnumeracionPropia"/>
    /// llaman <c>ambitoFactory.CreateScope()</c> justo antes de cada <c>Establecer</c>: cada
    /// tenant obtiene su propia instancia, inmune por construcción.</description></item>
    /// <item><description>Los 2 de <see cref="Categoria.ServicioDePlataformaConGuardaPropia"/>,
    /// 2 de los 3 de <see cref="Categoria.CredencialVerificadaInmediatamenteAntes"/>
    /// (<c>ApiKeyAuthenticationHandler</c>, <c>WebhookMicrosoft365Endpoints</c>) y
    /// <c>Program.cs</c> nunca visitan más de un tenant POR SU PROPIA llamada a
    /// <c>Establecer</c> — un circuito Blazor, un proceso CLI, una petición HTTP o, en
    /// <c>Program.cs</c>, el único <c>using</c> que siembra <c>TenantSeedData.IdPorDefecto</c>
    /// (línea aparte de los seeders que invoca después, cada uno con sus propias llamadas ya
    /// contadas por separado) no pueden aportar una "vuelta siguiente" que envenenar.</description></item>
    /// <item><description>Los 5 seeders (<c>AsignacionesOperativasBackfillSeeder</c>,
    /// <c>DatosPruebaSeeder</c>, <c>DelegacionDemoSeeder</c>, <c>DelegacionesSoporteSeeder</c>,
    /// <c>SegundoTenantSeeder</c>) de <see cref="Categoria.BootstrapOSiembra"/> SÍ visitan varios
    /// tenants con la misma instancia de <c>DbContext</c> (sin <c>CreateScope</c> entre ellos:
    /// cada uno recibe el <c>dbContextBootstrap</c> que <c>Program.cs</c> crea una sola vez y pasa
    /// de seeder en seeder), pero ninguno resuelve <c>IAlcanceDatosService</c> ni pasa por
    /// <c>IMediator</c> (comprobado por grep, cero apariciones ejecutables) — la precondición del
    /// mecanismo de REC-195 no se da.</description></item>
    /// <item><description><b>El único candidato real</b>: <c>WebhookWhatsAppEndpoints.cs</c> —
    /// el <c>foreach</c> sobre los fragmentos de un mismo payload de Meta puede resolver
    /// tenants distintos por vuelta, con el mismo <c>eventoRepositorio</c>/<c>unitOfWork</c>
    /// (mismo <c>CaeManagerDbContext</c>) inyectados una sola vez por petición. Reproducido antes
    /// de concluir nada (no solo leído): <c>WebhookLoopSinCreateScopeEntreTenantsTests</c>
    /// (<c>tests/CaeManager.IntegrationTests/MultiTenancy/</c>) confirma con Postgres real que, sin
    /// transacción explícita, cada <c>SaveChangesAsync</c> reabre físicamente la conexión y
    /// <c>TenantRlsConnectionInterceptor</c> refresca <c>app.tenant_id</c> al tenant vigente en
    /// cada vuelta — y su control positivo demuestra que el MISMO instrumento SÍ detecta el
    /// patrón peligroso (conexión mantenida abierta a mano entre vueltas) cuando existe.</description></item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, EntradaBlanca> Autorizados = new()
    {
        // ── DELEGACIÓN O CLIENTE YA VALIDADO (Application) ──────────────────────────
        ["src/CaeManager.Application/ApiKeys/Commands/GenerarClaveApi/GenerarClaveApiCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación"),
        ["src/CaeManager.Application/ApiKeys/Commands/RevocarClaveApi/RevocarClaveApiCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación"),
        ["src/CaeManager.Application/ApiKeys/Queries/ObtenerClavesApi/ObtenerClavesApiQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación"),
        ["src/CaeManager.Application/Dashboard/Queries/ObtenerDashboardEjecutivoQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "cliente.TenantId, del Cliente resuelto dentro del fan-out ya acotado a la cartera del operador"),
        ["src/CaeManager.Application/Dashboard/Queries/ObtenerKpisGlobalesQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "cliente.TenantId, mismo fan-out que ObtenerDashboardEjecutivoQuery"),
        ["src/CaeManager.Application/Bandeja/Queries/ObtenerMiTrabajoAgregado/ObtenerMiTrabajoAgregadoQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "tenant.TenantId, de ObtenerClientesAutorizadosQuery (cartera o delegación ya autorizadas), mismo fan-out que ObtenerKpisGlobalesQuery"),
        ["src/CaeManager.Application/AsistenteIa/Candidatos/ObtenerCandidatosAsistenteQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "tenant.TenantId, de ObtenerClientesAutorizadosQuery, mismo fan-out que ObtenerMiTrabajoAgregadoQuery; nunca el Tenant que nombre el texto de la orden"),
        ["src/CaeManager.Application/Tenants/Commands/AbrirAccesoSoporte/AbrirAccesoSoporteCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación de soporte"),
        ["src/CaeManager.Application/Tenants/Commands/CerrarAccesoSoporte/CerrarAccesoSoporteCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación de soporte"),
        ["src/CaeManager.Application/Tenants/Commands/CrearClienteDelegante/CrearClienteDeleganteCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "tenantCliente.Id, del Tenant recién creado por el propio comando en la misma transacción"),
        ["src/CaeManager.Application/Tenants/Commands/CrearOperadorCaeExterno/CrearOperadorCaeExternoCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "tenantOperador.Id, del Tenant recién creado por el propio comando tras la autorización global, en la misma transacción"),
        ["src/CaeManager.Application/Tenants/Commands/CrearTenantPropietarioDeOperadorCaeExterno/CrearTenantPropietarioDeOperadorCaeExternoCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "tenantPropietario.Id, del Tenant recién creado por el propio comando tras la autorización global (PuedeGlobalmenteAsync) y la validación del Operador, en la misma transacción"),
        ["src/CaeManager.Application/Operaciones/IncorporacionCartera/Commands/AceptarSolicitudIncorporacionCarteraCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "solicitud.PropietarioTenantId, tras cargar la solicitud filtrada por el Operador CAE de origen y autorizar al Coordinador CAE por su rol en ese origen"),
        ["src/CaeManager.Application/Operaciones/IncorporacionCartera/Commands/RevocarIncorporacionCarteraCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "solicitud.PropietarioTenantId, tras cargar la solicitud filtrada por el Operador CAE de origen y autorizar al Coordinador CAE o al propio Gestor CAE solicitante"),
        ["src/CaeManager.Application/Tenants/Queries/ObtenerActividadSoporte/ObtenerActividadSoporteQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, en OR con la vía del cliente visitado (ver UsosDeEsPlataformaCongeladosTests)"),

        // ── JOB DE FONDO SOBRE ENUMERACIÓN PROPIA (Infrastructure, HostedServices) ──
        ["src/CaeManager.Infrastructure/Alertas/EnvioAlertasVencimientoHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del bucle que recorre todos los tenants desde el propio servicio"),
        ["src/CaeManager.Infrastructure/DocumentosIa/ProcesadorAnalisisDocumentoHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "3 llamadas — tenantId del trabajo reclamado por SKIP LOCKED, nunca de un parámetro de petición"),
        ["src/CaeManager.Infrastructure/Operaciones/ExpiracionAsignacionesHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del bucle que recorre todos los Tenants (activos y suspendidos) desde el propio servicio, un CreateScope por Tenant propietario (2026-09-23: sin él, cae_app_runtime veía cero filas de AsignacionesOperacion/AsignacionesCartera)"),
        ["src/CaeManager.Infrastructure/Integraciones/IngestaWebhookHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "4 llamadas — tenantId del EventoWebhook ya persistido con su tenant resuelto por el paso de verificación previo"),
        ["src/CaeManager.Infrastructure/Integraciones/IngestaWebhookWhatsAppHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "4 llamadas — mismo patrón que IngestaWebhookHostedService (gate del interruptor, bucle principal, recuperación de estancados y registro de ejecución), completado en salud de plataforma (A-06, 2026-09-02)"),
        ["src/CaeManager.Infrastructure/Integraciones/RedaccionPayloadWebhookHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del EventoWebhook que procesa"),
        ["src/CaeManager.Infrastructure/Integraciones/RenovacionSuscripcionWebhookHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del EventoWebhook que procesa"),
        ["src/CaeManager.Infrastructure/Retencion/RetencionHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "3 llamadas — tenantId del bucle que recorre todos los tenants activos desde el propio servicio (HO-084-01, REC-084)"),
        ["src/CaeManager.Infrastructure/VigilanciaNormativa/VigilanciaNormativaBoeHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del bucle que recorre todos los tenants desde el propio servicio"),
        ["src/CaeManager.Infrastructure/Visitas/VigilanciaVisitasUrgentesHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del bucle que recorre todos los tenants desde el propio servicio"),

        // ── CREDENCIAL VERIFICADA INMEDIATAMENTE ANTES ──────────────────────────────
        ["src/CaeManager.Infrastructure/Autenticacion/ApiKeyAuthenticationHandler.cs"] =
            new(Categoria.CredencialVerificadaInmediatamenteAntes, "clave.TenantId, tras validar el hash de la clave de API recibida"),
        ["src/CaeManager.Web/Api/Integraciones/WebhookMicrosoft365Endpoints.cs"] =
            new(Categoria.CredencialVerificadaInmediatamenteAntes, "verificacion.TenantId, tras IWebhookTenantResolver.VerificarAsync comprobar clientState y subscriptionId"),
        ["src/CaeManager.Web/Api/Integraciones/WebhookWhatsAppEndpoints.cs"] =
            new(Categoria.CredencialVerificadaInmediatamenteAntes, "verificacion.TenantId, tras verificar la firma del webhook de WhatsApp"),

        // ── BOOTSTRAP O SIEMBRA ──────────────────────────────────────────────────────
        ["src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs"] =
            new(Categoria.BootstrapOSiembra, "tenants[0].Id, de la lista de tenants ya sembrados por el propio arranque"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs"] =
            new(Categoria.BootstrapOSiembra,
                "2 llamadas — tenant.Id de un tenant que el propio seeder acaba de crear, y " +
                "TenantSeedData.IdPorDefecto en SembrarInstruccionTratamientoIaTenantPrincipalAsync (HO-035-02, REC-035)"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionDemoSeeder.cs"] =
            new(Categoria.BootstrapOSiembra, "9 llamadas — todas sobre Ids de tenants de demo que el propio seeder crea o localiza"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionesSoporteSeeder.cs"] =
            new(Categoria.BootstrapOSiembra, "3 llamadas — Ids del tenant de plataforma y de tenants de demo, todos resueltos por el propio seeder"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/EscenariosDireccionDemoSeeder.cs"] =
            new(Categoria.BootstrapOSiembra, "3 llamadas — Ids del tenant del Operador CAE de la demo y de cada tenant propietario, todos aprovisionados o localizados por el propio seeder por nombre"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/SiembraDemoDireccionAdministrativa.cs"] =
            new(Categoria.BootstrapOSiembra, "2 llamadas — marcador de datos de demo sobre el Tenant que la propia siembra acaba de crear, localizado por nombre, y el Tenant propietario de cada rama al sembrar su historial de Comunicaciones y su ciclo documental (su Id lo devuelve la propia siembra); solo se alcanza desde el modo de CLI (ver SiembraDemoDireccionSoloDesdeElModoCliTests)"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/SegundoTenantSeeder.cs"] =
            new(Categoria.BootstrapOSiembra, "2 llamadas — tenantId del segundo tenant que el propio seeder crea"),
        ["src/CaeManager.Web/Program.cs"] =
            new(Categoria.BootstrapOSiembra, "TenantSeedData.IdPorDefecto, constante de siembra del tenant #1, solo en el arranque"),

        // ── SERVICIO DE PLATAFORMA CON GUARDA PROPIA ────────────────────────────────
        ["src/CaeManager.Infrastructure/MultiTenancy/RetiradaTenantDemoService.cs"] =
            new(Categoria.ServicioDePlataformaConGuardaPropia, "tenantId ya comprobado contra la allowlist de nombres de demo del propio servicio"),
        ["src/CaeManager.Web/Services/TrazaSoporteService.cs"] =
            new(Categoria.ServicioDePlataformaConGuardaPropia, "tenantId del tenant visitado de una sesión de soporte ya abierta y resuelta por ResolverSesionAsync"),

        // ── ADMIN PLATAFORMA VERIFICADO POR CONCESIÓN (Application, REC-035) ────────
        ["src/CaeManager.Application/Cumplimiento/Commands/RegistrarInstruccionTratamientoIaTenantPropietario/RegistrarInstruccionTratamientoIaTenantPropietarioCommand.cs"] =
            new(Categoria.AdminPlataformaVerificadoPorConcesion,
                "request.TenantPropietarioId, tras IAutorizacionAdminPlataforma.PuedeSobreTenantAsync confirmar la capacidad sobre ese tenant"),
        ["src/CaeManager.Application/Cumplimiento/Commands/RevocarInstruccionTratamientoIaTenantPropietario/RevocarInstruccionTratamientoIaTenantPropietarioCommand.cs"] =
            new(Categoria.AdminPlataformaVerificadoPorConcesion,
                "request.TenantPropietarioId, mismo criterio que Registrar"),
        ["src/CaeManager.Application/Cumplimiento/Queries/ObtenerHistoricoInstruccionTratamientoIaTenantPropietario/ObtenerHistoricoInstruccionTratamientoIaTenantPropietarioQuery.cs"] =
            new(Categoria.AdminPlataformaVerificadoPorConcesion,
                "request.TenantPropietarioId, mismo criterio que Registrar — lectura cruzada, no solo escritura"),

        // ── TENANT DE ORIGEN DEL USUARIO (Application) ──────────────────────────────
        ["src/CaeManager.Application/Operaciones/IncorporacionCartera/ContextoOperadorCae.cs"] =
            new(Categoria.TenantDeOrigenDelUsuario,
                "2 llamadas — ObtenerTenantOrigenIdAsync() para leer el rol de sesión en la propia organización, y el OperadorTenantId que esa misma resolución guardó, para las escrituras del Operador CAE"),
    };

    private static readonly Regex LlamadaAEstablecer = new(
        @"AmbitoTenantExplicito\.Establecer\(", RegexOptions.Compiled);

    private static readonly string[] DirectoriosVigilados = ["src"];

    /// <summary>
    /// Analiza el árbol sintáctico (igual que <c>TerminologiaCanonicaTests</c>,
    /// DEC-65/REC-178: "instrumentación léxica/sintáctica, no una regex más
    /// sofisticada") para comprobar que <paramref name="textoFuente"/> declara
    /// al menos un método con el atributo <c>[Fact]</c> aplicado de verdad —
    /// no que la cadena <c>"[Fact]"</c> aparezca en algún sitio del fichero.
    ///
    /// <para>
    /// Reemplaza dos intentos con regex, los dos rechazados por Codex
    /// (2026-09-18): un <c>Contains</c> simple aceptaba <c>// [Fact]</c> en
    /// un comentario o la cadena literal en un mensaje de aserción; anclar la
    /// regex a "sola en su línea" seguía aceptando un comentario de BLOQUE
    /// (<c>/* [Fact] */</c> repartido en varias líneas) o un literal
    /// raw/verbatim multilínea que contuviera esa línea — ninguna regex sobre
    /// texto puede distinguir "dentro de un comentario" de "aplicado a un
    /// método real" sin analizar la sintaxis.
    /// </para>
    /// </summary>
    private static bool DeclaraAlMenosUnMetodoConFact(string textoFuente)
    {
        var root = CSharpSyntaxTree.ParseText(textoFuente).GetRoot();

        return root.DescendantNodes()
            .OfType<MethodDeclarationSyntax>()
            .Any(metodo => metodo.AttributeLists
                .SelectMany(lista => lista.Attributes)
                .Any(atributo => EsAtributoFact(atributo.Name.ToString())));
    }

    /// <summary>
    /// <c>Fact</c>/<c>FactAttribute</c>, con o sin el prefijo de espacio de
    /// nombres <c>Xunit.</c> — las cuatro formas válidas de escribir el
    /// atributo en C#.
    /// </summary>
    private static bool EsAtributoFact(string nombre) =>
        nombre is "Fact" or "FactAttribute" or "Xunit.Fact" or "Xunit.FactAttribute";

    private static Dictionary<string, int> LlamadasPorFichero()
    {
        var raiz = RaizDelRepositorio();
        var resultado = new Dictionary<string, int>();

        foreach (var directorio in DirectoriosVigilados)
        {
            foreach (var ruta in Directory.EnumerateFiles(
                Path.Combine(raiz, directorio), "*.cs", SearchOption.AllDirectories))
            {
                var contenido = File.ReadAllText(ruta);
                var apariciones = LlamadaAEstablecer.Matches(contenido).Count;
                if (apariciones == 0)
                    continue;

                var relativa = Path.GetRelativePath(raiz, ruta).Replace('\\', '/');
                resultado[relativa] = apariciones;
            }
        }

        return resultado;
    }

    [Fact]
    public void Solo_los_ficheros_de_la_lista_blanca_establecen_un_tenant_explicito()
    {
        var encontrados = LlamadasPorFichero();

        // Guarda del instrumento: si el patrón dejara de reconocer llamadas, la
        // comparación de igualdad de abajo pasaría comparando dos vacíos.
        encontrados.Should().NotBeEmpty(
            "src/ tiene que seguir teniendo llamadas a AmbitoTenantExplicito.Establecer; " +
            "un resultado vacío significa que el patrón ya no ve lo que dice vigilar, no que se hayan retirado todas");

        encontrados.Keys.Should().BeEquivalentTo(Autorizados.Keys,
            "un fichero nuevo que llame a Establecer con un Guid sin verificar cambiaría el tenant activo " +
            "(filtro de EF y RLS) sin que ninguna autorización lo respalde; añadirlo aquí exige escribir de " +
            "dónde sale ese Guid, en el mismo commit que lo introduce");
    }

    [Fact]
    public void El_numero_de_llamadas_por_fichero_no_crece_en_silencio()
    {
        var encontrados = LlamadasPorFichero();

        // Solo se comprueban los ficheros que ya están en la lista blanca: si uno
        // nuevo apareciera, el test anterior ya lo señala con más detalle, y
        // duplicar esa aserción aquí solo generaría dos fallos por el mismo hecho.
        var conocidos = encontrados.Keys.Intersect(Autorizados.Keys);

        var conteoEsperado = new Dictionary<string, int>
        {
            ["src/CaeManager.Infrastructure/DocumentosIa/ProcesadorAnalisisDocumentoHostedService.cs"] = 3,
            ["src/CaeManager.Infrastructure/Integraciones/IngestaWebhookHostedService.cs"] = 4,
            ["src/CaeManager.Infrastructure/Integraciones/IngestaWebhookWhatsAppHostedService.cs"] = 4,
            ["src/CaeManager.Infrastructure/Retencion/RetencionHostedService.cs"] = 3,
            ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionDemoSeeder.cs"] = 10,
            ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionesSoporteSeeder.cs"] = 3,
            ["src/CaeManager.Infrastructure/Persistence/Seed/SegundoTenantSeeder.cs"] = 2,
            ["src/CaeManager.Infrastructure/Persistence/Seed/EscenariosDireccionDemoSeeder.cs"] = 3,
            ["src/CaeManager.Infrastructure/Persistence/Seed/SiembraDemoDireccionAdministrativa.cs"] = 2,
            ["src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs"] = 2,
            ["src/CaeManager.Application/Operaciones/IncorporacionCartera/ContextoOperadorCae.cs"] = 2,
        };

        foreach (var fichero in conocidos)
        {
            var esperado = conteoEsperado.GetValueOrDefault(fichero, 1);
            encontrados[fichero].Should().Be(esperado,
                $"{fichero} tenía {esperado} llamada(s) a Establecer verificadas una a una; un número " +
                "distinto significa que se añadió o quitó una sin actualizar esta lista");
        }
    }

    /// <summary>
    /// REC-212: sitios fuera de Application que reutilizan la MISMA instancia
    /// de scope/<c>DbContext</c> para <b>más de un tenant</b> sin
    /// <c>CreateScope()</c> entre vueltas — la forma exacta del defecto de
    /// REC-195, aunque aquí ninguno resuelva <c>IAlcanceDatosService</c> (ver
    /// el doc-comment de la clase). Lista cerrada y verificada uno a uno, no
    /// derivada de <see cref="Autorizados"/> por regex: "varias vueltas en el
    /// mismo scope" no se detecta con un patrón de texto sin dar falsos
    /// positivos (un <c>foreach</c> de un seeder que sí crea su propio
    /// contexto por vuelta, por ejemplo).
    ///
    /// Cada entrada exige su propio test de integración que reproduzca —no
    /// solo lea— que la reutilización no envenena nada, igual que
    /// <c>AlcanceMemoizadoPorTenantEnFanOutTests</c> para el fan-out de
    /// Application.
    ///
    /// <para>
    /// <b>Qué comprueba el <c>Fact</c> de abajo y qué NO</b> (revisión de
    /// Codex, 2026-09-18: la primera versión descartaba el nombre del test
    /// con <c>_ = test;</c> sin comprobar nada de él, así que borrar,
    /// renombrar o vaciar el test de reproducción dejaba esto en verde
    /// igual). Ahora comprueba que el fichero de test **existe** y declara
    /// al menos un <c>[Fact]</c> — no que ese test **pase**: eso lo exige el
    /// gate de CI de <c>CaeManager.IntegrationTests</c>, no este proyecto,
    /// que no referencia aquel y no puede ejecutarlo. Y comprueba por
    /// <c>BeSubsetOf</c>, no por igualdad, contra <see cref="Autorizados"/>:
    /// eso valida que esta lista nunca nombre un fichero que no esté en la
    /// lista blanca de arriba, pero **no** detecta que aparezca un candidato
    /// nuevo (un fichero recién añadido a <see cref="Autorizados"/> que
    /// también reutilice su instancia entre varios tenants) sin que nadie lo
    /// añada aquí — eso sigue exigiendo la misma lectura manual que
    /// descubrió a <c>WebhookWhatsAppEndpoints.cs</c>, porque "visita varios
    /// tenants sin <c>CreateScope</c>" no es una propiedad detectable por
    /// regex sin falsos positivos (ver el párrafo de arriba).
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> SitiosConVariosTenantsPorInstancia = new()
    {
        ["src/CaeManager.Web/Api/Integraciones/WebhookWhatsAppEndpoints.cs"] =
            "tests/CaeManager.IntegrationTests/MultiTenancy/WebhookLoopSinCreateScopeEntreTenantsTests.cs",
    };

    [Fact]
    public void Los_sitios_con_varios_tenants_por_instancia_tienen_su_test_de_reproduccion()
    {
        SitiosConVariosTenantsPorInstancia.Keys.Should().BeSubsetOf(Autorizados.Keys,
            "esta lista es un recorte de la lista blanca de arriba, nunca puede nombrar un fichero que no esté allí");

        var raiz = RaizDelRepositorio();

        foreach (var (ficheroProduccion, ficheroTest) in SitiosConVariosTenantsPorInstancia)
        {
            var rutaProduccion = Path.Combine(raiz, ficheroProduccion);
            File.Exists(rutaProduccion).Should().BeTrue(
                $"{ficheroProduccion} tiene que seguir existiendo — si se movió o se borró, esta entrada quedó huérfana");

            // Una sola aparición SINTÁCTICA de "Establecer(" no contradice que
            // se ejecute varias veces en tiempo de ejecución — aquí es
            // exactamente eso: UN único using dentro de un foreach sobre los
            // fragmentos del payload, no varias llamadas de texto distintas
            // como en un seeder. Por eso esta lista no se deriva del recuento
            // de Autorizados y hace falta el test de reproducción nombrado
            // arriba, no una comprobación estática, para demostrar que la
            // reutilización del scope entre vueltas no envenena nada.
            LlamadaAEstablecer.IsMatch(File.ReadAllText(rutaProduccion)).Should().BeTrue(
                $"{ficheroProduccion} tiene que seguir llamando a AmbitoTenantExplicito.Establecer — si ya no lo hace, " +
                "esta entrada quedó huérfana");

            var rutaTest = Path.Combine(raiz, ficheroTest);
            File.Exists(rutaTest).Should().BeTrue(
                $"{ficheroTest} tiene que existir — es el test de reproducción que demuestra que {ficheroProduccion} " +
                "no envenena nada; si se borró o se movió sin actualizar esta entrada, la afirmación queda sin comprobar");

            // Análisis sintáctico, no regex (2ª corrección tras Codex,
            // 2026-09-18 — ver el doc-comment de DeclaraAlMenosUnMetodoConFact):
            // exige un FactAttribute aplicado de verdad a un método, no la
            // cadena "[Fact]" en un comentario, una cadena literal o un
            // comentario de bloque multilínea.
            DeclaraAlMenosUnMetodoConFact(File.ReadAllText(rutaTest)).Should().BeTrue(
                $"{ficheroTest} tiene que declarar al menos un método con [Fact] — vaciarlo o convertirlo en una " +
                "clase sin tests pasaría la comprobación de existencia sin demostrar nada");
        }
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
