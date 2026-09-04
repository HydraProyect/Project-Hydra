using System.Text.RegularExpressions;
using FluentAssertions;

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
/// —varios de ellos con 2 o más, como <c>DelegacionDemoSeeder</c> con 9— sí añadiría
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
        ["src/CaeManager.Application/Tenants/Commands/AbrirAccesoSoporte/AbrirAccesoSoporteCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación de soporte"),
        ["src/CaeManager.Application/Tenants/Commands/CerrarAccesoSoporte/CerrarAccesoSoporteCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, tras cargar y autorizar la Delegación de soporte"),
        ["src/CaeManager.Application/Tenants/Commands/CrearClienteDelegante/CrearClienteDeleganteCommand.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "tenantCliente.Id, del Tenant recién creado por el propio comando en la misma transacción"),
        ["src/CaeManager.Application/Tenants/Queries/ObtenerActividadSoporte/ObtenerActividadSoporteQuery.cs"] =
            new(Categoria.DelegacionOClienteYaValidado, "delegacion.TenantClienteId, en OR con la vía del cliente visitado (ver UsosDeEsPlataformaCongeladosTests)"),

        // ── JOB DE FONDO SOBRE ENUMERACIÓN PROPIA (Infrastructure, HostedServices) ──
        ["src/CaeManager.Infrastructure/Alertas/EnvioAlertasVencimientoHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "tenantId del bucle que recorre todos los tenants desde el propio servicio"),
        ["src/CaeManager.Infrastructure/DocumentosIa/ProcesadorAnalisisDocumentoHostedService.cs"] =
            new(Categoria.JobDeFondoSobreEnumeracionPropia, "3 llamadas — tenantId del trabajo reclamado por SKIP LOCKED, nunca de un parámetro de petición"),
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
    };

    private static readonly Regex LlamadaAEstablecer = new(
        @"AmbitoTenantExplicito\.Establecer\(", RegexOptions.Compiled);

    private static readonly string[] DirectoriosVigilados = ["src"];

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
            ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionDemoSeeder.cs"] = 9,
            ["src/CaeManager.Infrastructure/Persistence/Seed/DelegacionesSoporteSeeder.cs"] = 3,
            ["src/CaeManager.Infrastructure/Persistence/Seed/SegundoTenantSeeder.cs"] = 2,
            ["src/CaeManager.Infrastructure/Persistence/Seed/DatosPruebaSeeder.cs"] = 2,
        };

        foreach (var fichero in conocidos)
        {
            var esperado = conteoEsperado.GetValueOrDefault(fichero, 1);
            encontrados[fichero].Should().Be(esperado,
                $"{fichero} tenía {esperado} llamada(s) a Establecer verificadas una a una; un número " +
                "distinto significa que se añadió o quitó una sin actualizar esta lista");
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
