using System.Net;
using System.Security.Claims;
using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.DependencyInjection;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Web.Api.V1;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.ApiKeys;

/// <summary>
/// <b>La API pública <c>/api/v1</c> autenticando por HTTP bajo el rol de
/// tráfico real (<c>cae_app_runtime</c>), no bajo el propietario.</b>
///
/// <para>
/// El hueco que estos tests cierran es de instrumento antes que de producto:
/// <c>GestionClavesApiTests</c> ejercita los comandos de gestión de claves
/// pero nunca hace una llamada HTTP, y ningún E2E cubre <c>/api/v1</c>. Toda
/// la evidencia que había sobre "la clave autentica" venía de una sesión
/// conectada como <c>postgres</c> —superusuario, <c>BYPASSRLS</c> efectivo—,
/// donde las políticas de <c>HabilitarRlsClavesApi</c> no llegan a evaluarse.
/// Producción conecta con <c>cae_app_runtime</c> (<c>NOSUPERUSER
/// NOBYPASSRLS</c>) desde 2026-08-14, así que el instrumento y el sistema
/// medido no coincidían justo en la variable que decide el resultado.
/// </para>
///
/// <para>
/// <b>Por qué el rol es la única variable.</b> Los dos primeros tests montan
/// el MISMO arnés —mismo cableado de producción, mismo endpoint real, mismo
/// <see cref="ApiKeyAuthenticationHandler"/>, misma clave emitida por el
/// comando real— y solo cambian la cadena de conexión del tráfico. Si uno
/// pasa y el otro no, la diferencia no puede atribuirse al arnés.
/// </para>
///
/// <para>
/// <b>Límites declarados del arnés</b>, para que nadie lea de aquí más de lo
/// que mide: no monta Blazor (el <c>AuthenticationStateProvider</c> es un
/// doble que devuelve anónimo, que es lo que ve cualquier petición de API
/// mínima fuera de un circuito), no monta la cookie de Identity, no aplica el
/// rate limiting del grupo real, y retira los <c>IHostedService</c> de
/// Infrastructure —este test mide el camino de una petición, no los workers de
/// fondo—. Lo que sí reproduce es lo que la hipótesis pone en duda: la
/// conexión, sus interceptores, RLS efectiva y el handler de autenticación.
/// </para>
/// </summary>
public class ApiPublicaBajoRolRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantClienteId;
    private string _claveEnClaro = string.Empty;
    private Guid _delegacionId;
    private Guid _tenantPlataformaId;
    private Guid _emisorId;

    private const string RazonSocialDelCliente = "Empresa Atlas S.L.";

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContextoPropietario();
        await contexto.Database.MigrateAsync();

        var tenantPlataforma = new Tenant("TALVEG", esPlataforma: true);
        var tenantCliente = new Tenant("Tenant beneficiario");
        contexto.Tenants.AddRange(tenantPlataforma, tenantCliente);

        var delegacion = DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantCliente.Id);
        delegacion.ActivarParaSoporte("Prueba de integración", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
        contexto.DelegacionesTenant.Add(delegacion);
        var emisorId = Guid.NewGuid();
        contexto.Users.Add(new ApplicationUser { Id = emisorId, TenantId = tenantPlataforma.Id, UserName = "emisor-alfa@ejemplo.test", Email = "emisor-alfa@ejemplo.test" });
        await contexto.SaveChangesAsync();

        _emisorId = emisorId;
        _tenantClienteId = tenantCliente.Id;
        _delegacionId = delegacion.Id;
        _tenantPlataformaId = tenantPlataforma.Id;

        // La Empresa contraparte que la API tiene que devolver, sellada contra
        // el Tenant propietario de la clave.
        using (AmbitoTenantExplicito.Establecer(_tenantClienteId))
        {
            contexto.Empresas.Add(
                Empresa.CrearComoCliente(RazonSocialDelCliente, "B10000016", esCritico: false, notas: null, ejecutivoUsuarioId: null));

            // El listado de Clientes calcula estado documental vía
            // ObtenerAlertasQuery, que exige EXACTAMENTE una fila de parámetros
            // por Tenant (SingleAsync) — la crean los comandos de alta de
            // Tenant. Sin ella la petición muere con 500 mucho después de
            // autenticar, por un hueco de siembra que nada tiene que ver con
            // el rol de conexión.
            contexto.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 7));
            await contexto.SaveChangesAsync();
        }

        // La clave la emite el comando real, no una construcción a mano: el
        // hash que va a la fila es el que el handler de autenticación vuelve a
        // calcular después.
        _claveEnClaro = await EmitirClaveAsync(contexto);
    }

    private async Task<string> EmitirClaveAsync(CaeManagerDbContext contexto)
    {
        var generador = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), new AutorizacionClavesApiDePrueba(_tenantClienteId),
            new CurrentUserServiceFalso(_emisorId, tenantOrigenId: _tenantPlataformaId), contexto);

        var generada = await generador.Handle(
            new GenerarClaveApiCommand(_delegacionId, "Integración externa de prueba", DateTime.UtcNow.AddDays(30), "Emisión de prueba"), CancellationToken.None);

        generada.EsExitoso.Should().BeTrue("sin una clave emitida no hay nada que autenticar");
        return generada.Valor.ClaveEnClaro;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaPropietario);

    /// <summary>
    /// El caso de producción: tráfico bajo <c>cae_app_runtime</c>, con RLS
    /// efectiva sobre <c>ClavesApi</c>.
    /// </summary>
    [Fact]
    public async Task Con_el_rol_runtime_una_clave_valida_autentica_y_devuelve_la_cartera_del_tenant()
    {
        var (estado, cuerpo) = await LlamarAsync(
            BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario), _claveEnClaro);

        estado.Should().Be(HttpStatusCode.OK,
            "una clave vigente autentica contra /api/v1 con el rol de tráfico real; un 401 aquí significa " +
            "que la resolución hash→clave no sobrevive a la política RLS de ClavesApi");
        cuerpo.Should().Contain(RazonSocialDelCliente,
            "autenticar no basta: el tenant resuelto desde la clave tiene que ser el que ve su propia cartera");
    }

    /// <summary>
    /// Control del instrumento. Mismo arnés y misma clave, conectando como
    /// propietario (<c>postgres</c>, superusuario: RLS no se le aplica). Si
    /// este pasa y el de arriba no, el arnés funciona y el rol es la causa;
    /// si fallaran los dos, el hallazgo estaría en el arnés y no en RLS.
    /// </summary>
    [Fact]
    public async Task Control_del_instrumento_con_el_rol_propietario_la_misma_clave_autentica()
    {
        var (estado, cuerpo) = await LlamarAsync(_cadenaPropietario, _claveEnClaro);

        estado.Should().Be(HttpStatusCode.OK, "el arnés tiene que ser capaz de responder 200 a alguien; " +
            "si no, el 401 del otro test no probaría nada sobre el rol. Detalle: {0}", cuerpo);
        cuerpo.Should().Contain(RazonSocialDelCliente);
    }

    /// <summary>
    /// La otra dirección del contrato: resolver la clave sin depender del
    /// tenant no puede convertirse en "cualquier cadena entra". Sin este caso,
    /// una corrección que devolviera siempre una clave dejaría verde el primer
    /// test.
    /// </summary>
    [Fact]
    public async Task Con_el_rol_runtime_una_clave_inexistente_no_autentica()
    {
        var (estado, _) = await LlamarAsync(
            BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario),
            "hydra_0000000000000000000000000000000000000000000000000000000000000000");

        estado.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// La clave vale mientras siga abierta la ventana de Soporte TALVEG en que se emitió
    /// (<c>CriterioSoporteClaveApi.PuedeAutenticar</c>): al cerrarla, al vencer o al reabrirla queda
    /// en 401 aunque no esté caducada.
    /// </summary>
    [Theory]
    [InlineData("soporte revocado")]
    [InlineData("soporte vencido")]
    [InlineData("soporte reabierto")]
    public async Task Bajo_RLS_la_clave_cae_con_la_ventana_de_soporte_en_que_se_emitio(string caso)
    {
        await using (var contexto = CrearContextoPropietario())
        {
            var delegacion = await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacionId);
            delegacion.Desactivar();
            if (caso == "soporte vencido")
                delegacion.ActivarParaSoporte("Prueba de vencimiento", DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(-2));
            if (caso == "soporte reabierto")
                delegacion.ActivarParaSoporte("Prueba de reapertura", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var (estado, cuerpo) = await LlamarAsync(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario), _claveEnClaro);
        estado.Should().Be(HttpStatusCode.Unauthorized, "caso {0}. Detalle: {1}", caso, cuerpo);
    }

    /// <summary>
    /// Decisión del 2026-10-28 concretada el 2026-10-31: la suspensión operativa corta y reactivar
    /// no devuelve la clave; Suspendida y SoloLectura cortan y al revertirse vuelve a valer.
    /// </summary>
    [Theory]
    [InlineData("suspendido", HttpStatusCode.Unauthorized)]
    [InlineData("reactivado", HttpStatusCode.Unauthorized)]
    [InlineData("comercial suspendida", HttpStatusCode.Unauthorized)]
    [InlineData("comercial revertida", HttpStatusCode.OK)]
    [InlineData("solo lectura", HttpStatusCode.Unauthorized)]
    [InlineData("solo lectura revertida", HttpStatusCode.OK)]
    public async Task Bajo_RLS_el_estado_del_Tenant_propietario_decide_la_clave(string caso, HttpStatusCode esperado)
    {
        await using (var contexto = CrearContextoPropietario())
        {
            var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantClienteId);
            switch (caso)
            {
                case "suspendido":
                    tenant.Suspender();
                    break;
                case "reactivado":
                    tenant.Suspender();
                    tenant.Reactivar();
                    break;
                default:
                    tenant.VincularSuscripcionStripe("cus_prueba", "sub_prueba", EstadoComercialTenant.Activa);
                    tenant.ActualizarEstadoComercial(caso.StartsWith("solo lectura", StringComparison.Ordinal) ? EstadoComercialTenant.SoloLectura : EstadoComercialTenant.Suspendida);
                    if (caso.EndsWith("revertida", StringComparison.Ordinal)) tenant.ActualizarEstadoComercial(EstadoComercialTenant.Activa);
                    break;
            }
            await contexto.SaveChangesAsync();
        }

        var (estado, cuerpo) = await LlamarAsync(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario), _claveEnClaro);
        estado.Should().Be(esperado, "caso {0}. Detalle: {1}", caso, cuerpo);
    }

    [Fact]
    public async Task Bajo_RLS_la_clave_emitida_tras_reactivar_el_Tenant_autentica()
    {
        await using var contexto = CrearContextoPropietario();
        var tenant = await contexto.Tenants.SingleAsync(t => t.Id == _tenantClienteId);
        tenant.Suspender();
        tenant.Reactivar();
        await contexto.SaveChangesAsync();
        var claveNueva = await EmitirClaveAsync(contexto);

        var cadena = BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario);
        (await LlamarAsync(cadena, _claveEnClaro)).Estado.Should().Be(HttpStatusCode.Unauthorized, "control: la anterior sigue cortada");
        (await LlamarAsync(cadena, claveNueva)).Estado.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("solo soporte de otro origen")]
    [InlineData("solo delegacion comercial del mismo origen")]
    [InlineData("ninguna delegacion")]
    [InlineData("clave caducada")]
    public async Task Bajo_RLS_rechaza_la_clave_caducada_o_sin_delegacion_de_soporte_de_su_origen(string caso)
    {
        await using (var contexto = CrearContextoPropietario())
        {
            if (caso == "clave caducada")
            {
                using var ambito = AmbitoTenantExplicito.Establecer(_tenantClienteId);
                var clave = await contexto.ClavesApi.SingleAsync();
                contexto.Entry(clave).Property(c => c.CaducaEnUtc).CurrentValue = DateTime.UtcNow.AddDays(-1);
                await contexto.SaveChangesAsync();
            }
            else
            {
                // La delegación de Soporte del origen emisor desaparece; en su lugar queda, según el
                // caso, otra que no cumple PuedeAutenticar o ninguna.
                contexto.DelegacionesTenant.Remove(await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacionId));
                await contexto.SaveChangesAsync();
                if (caso == "solo soporte de otro origen")
                {
                    var otroOrigen = new Tenant("Organización Beta");
                    contexto.Tenants.Add(otroOrigen);
                    var otra = DelegacionTenant.ParaSoporte(otroOrigen.Id, _tenantClienteId);
                    otra.ActivarParaSoporte("Prueba de otro origen", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);
                    contexto.DelegacionesTenant.Add(otra);
                }
                if (caso == "solo delegacion comercial del mismo origen")
                    contexto.DelegacionesTenant.Add(new DelegacionTenant(_tenantPlataformaId, _tenantClienteId));
                await contexto.SaveChangesAsync();
            }
        }

        var (estado, _) = await LlamarAsync(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario), _claveEnClaro);
        estado.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// M-35, con el rol real: la cuenta emisora es de otra organización y su fila no se ve desde
    /// el Tenant propietario de la clave, así que el corte solo existe si
    /// <c>app_estado_cuenta_emisora</c> llega a responder bajo <c>cae_app_runtime</c>.
    /// </summary>
    [Theory]
    [InlineData("desactivada", HttpStatusCode.Unauthorized)]
    [InlineData("reactivada", HttpStatusCode.Unauthorized)]
    [InlineData("bloqueo temporal", HttpStatusCode.OK)]
    public async Task Bajo_RLS_el_estado_de_la_cuenta_emisora_decide_la_clave(string caso, HttpStatusCode esperado)
    {
        await using (var contexto = CrearContextoPropietario())
        {
            var cuenta = await contexto.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _emisorId);
            if (caso == "bloqueo temporal")
                cuenta.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
            else
                cuenta.Desactivar();
            if (caso == "reactivada")
                cuenta.Reactivar();
            await contexto.SaveChangesAsync();
        }

        var (estado, cuerpo) = await LlamarAsync(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario), _claveEnClaro);
        estado.Should().Be(esperado, "caso {0}. Detalle: {1}", caso, cuerpo);
    }

    [Fact]
    public async Task Bajo_RLS_la_clave_emitida_tras_reactivar_la_cuenta_autentica()
    {
        await using var contexto = CrearContextoPropietario();
        var cuenta = await contexto.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == _emisorId);
        cuenta.Desactivar();
        cuenta.Reactivar();
        await contexto.SaveChangesAsync();
        var claveNueva = await EmitirClaveAsync(contexto);

        var cadena = BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario);
        (await LlamarAsync(cadena, _claveEnClaro)).Estado.Should().Be(HttpStatusCode.Unauthorized, "control: la anterior sigue cortada");
        (await LlamarAsync(cadena, claveNueva)).Estado.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Contrato de catálogo: si se perdiera el <c>search_path</c> fijado o el
    /// <c>REVOKE ... FROM PUBLIC</c>, los tests de comportamiento seguirían en verde y cualquier
    /// rol con conexión podría preguntar por el estado de cualquier cuenta.
    /// </summary>
    [Fact]
    public async Task La_funcion_del_estado_de_la_cuenta_emisora_tiene_el_contrato_de_las_definidoras()
    {
        await using var propietario = new NpgsqlConnection(_cadenaPropietario);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT p.prosecdef, array_to_string(p.proconfig, ';'),
                   has_function_privilege('cae_app_runtime', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_soporte', p.oid, 'EXECUTE'),
                   has_function_privilege('cae_app_aprovisionamiento', p.oid, 'EXECUTE'),
                   p.proacl IS NULL OR EXISTS (SELECT 1 FROM aclexplode(p.proacl) a WHERE a.grantee = 0)
            FROM pg_proc p
            WHERE p.proname = 'app_estado_cuenta_emisora' AND p.pronamespace = 'public'::regnamespace;
            """, propietario);
        await using var lector = await orden.ExecuteReaderAsync();

        (await lector.ReadAsync()).Should().BeTrue("la función tiene que existir");
        lector.GetBoolean(0).Should().BeTrue("SECURITY DEFINER");
        lector.GetString(1).Should().Contain("search_path");
        lector.GetBoolean(2).Should().BeTrue("la autenticación corre con cae_app_runtime");
        lector.GetBoolean(3).Should().BeTrue("el listado del Actor de Plataforma TALVEG evalúa el respaldo en Sesión Privilegiada");
        lector.GetBoolean(4).Should().BeFalse("el aprovisionamiento no autentica claves");
        lector.GetBoolean(5).Should().BeFalse("nada para PUBLIC");
    }

    // Otra ventana del mismo origen, abierta ya al emitir, no respalda la clave si se cierra la suya;
    // tampoco la tumba si la suya sigue abierta.
    [Theory]
    [InlineData(true, HttpStatusCode.Unauthorized)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task Bajo_RLS_solo_cuenta_la_ventana_en_que_se_emitio_entre_varias_del_mismo_origen(bool cerrarLaDeLaEmision, HttpStatusCode esperado)
    {
        await using (var contexto = CrearContextoPropietario())
        {
            var otra = DelegacionTenant.ParaSoporte(_tenantPlataformaId, _tenantClienteId);
            otra.ActivarParaSoporte("Otra ventana de prueba", DateTime.UtcNow.AddHours(1), DateTime.UtcNow.AddHours(-1));
            contexto.DelegacionesTenant.Add(otra);
            if (cerrarLaDeLaEmision)
                (await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacionId)).Desactivar();
            await contexto.SaveChangesAsync();
        }

        var (estado, _) = await LlamarAsync(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario), _claveEnClaro);
        estado.Should().Be(esperado);
    }

    /// <summary>
    /// Levanta el host con el cableado de producción sobre la cadena de
    /// tráfico indicada y hace UNA llamada real por HTTP.
    /// </summary>
    private async Task<(HttpStatusCode Estado, string Cuerpo)> LlamarAsync(string cadenaDeTrafico, string clave)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:CaeManagerDb"] = _cadenaPropietario,
            ["ConnectionStrings:CaeManagerDbRuntime"] = cadenaDeTrafico,
            // WebApplication.CreateBuilder() arranca en Production: sin esto, el registro
            // de Data Protection se niega por falta de certificado (P1-F3), que aquí no se prueba.
            ["DataProtection:PermitirClavesSinCifrar"] = "true",
        });

        builder.Services.AddApplication();
        builder.Services.AddInfrastructure(builder.Configuration, builder.Environment);

        // Los workers de fondo de Infrastructure no participan en el camino de
        // una petición y sí competirían por la misma base. Se retiran por
        // ensamblado, nunca con RemoveAll<IHostedService>(): eso se llevaría
        // por delante el servicio que arranca Kestrel.
        foreach (var descriptor in builder.Services
                     .Where(s => s.ServiceType == typeof(IHostedService)
                         && s.ImplementationType?.Assembly == typeof(InfrastructureServiceCollectionExtensions).Assembly)
                     .ToList())
        {
            builder.Services.Remove(descriptor);
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddScoped<AuthenticationStateProvider, SinCircuitoDeBlazor>();
        builder.Services.AddScoped<IClienteActivoSeleccionado, SinClienteActivo>();
        builder.Services.AddScoped<ICurrentUserService, CaeManager.Web.Services.CurrentUserService>();
        builder.Services.AddScoped<IActorAuditoria, CaeManager.Web.Services.ActorAuditoriaDesdeSesion>();
        builder.Services.AddScoped<ITenantActual, CaeManager.Web.Services.TenantActual>();

        builder.Services
            .AddAuthentication(ApiKeyAuthenticationSchemeOptions.NombreEsquema)
            .AddScheme<ApiKeyAuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationSchemeOptions.NombreEsquema, _ => { });

        builder.Services.AddAuthorizationBuilder()
            .AddPolicy("ApiPublica", policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationSchemeOptions.NombreEsquema)
                .RequireAuthenticatedUser());

        await using var app = builder.Build();

        // Sin esto, cualquier excepción del camino de la petición llega al test
        // como un 500 opaco y la investigación empieza a ciegas. El fallo se
        // relanza: el pipeline responde igual que en producción.
        var excepciones = new List<string>();
        app.Use(async (contexto, siguiente) =>
        {
            try
            {
                await siguiente(contexto);
            }
            catch (Exception ex)
            {
                excepciones.Add(ex.ToString());
                throw;
            }
        });

        app.UseAuthentication();
        app.UseAuthorization();

        app.MapGroup("/api/v1")
            .RequireAuthorization("ApiPublica")
            .AddEndpointFilter<ActorIntegracionExternaEndpointFilter>()
            .MapClientesApiEndpoints();

        await app.StartAsync();
        try
        {
            var direccion = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            using var http = new HttpClient { BaseAddress = new Uri(direccion) };
            http.DefaultRequestHeaders.Add("Authorization", $"ApiKey {clave}");

            var respuesta = await http.GetAsync("/api/v1/clientes");
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            return (respuesta.StatusCode, excepciones.Count > 0 ? string.Join("\n", excepciones) : cuerpo);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private CaeManagerDbContext CrearContextoPropietario()
    {
        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>
    /// Lo que <c>CurrentUserService</c> y <c>TenantActual</c> encuentran en una
    /// petición de API mínima: no hay circuito de Blazor, así que el estado de
    /// autenticación del proveedor es anónimo y ambos caen al
    /// <c>HttpContext.User</c> — que es justo el camino de producción para
    /// <c>/api/v1</c>.
    /// </summary>
    private sealed class SinCircuitoDeBlazor : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    /// <summary>Una petición con clave no trae workspace seleccionado ni sesión privilegiada.</summary>
    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
