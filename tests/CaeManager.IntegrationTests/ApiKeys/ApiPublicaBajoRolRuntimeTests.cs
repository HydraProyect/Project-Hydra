using System.Net;
using System.Security.Claims;
using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Tenants;
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

    private const string RazonSocialDelCliente = "Refrielectric S.L.";

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContextoPropietario();
        await contexto.Database.MigrateAsync();

        var tenantPlataforma = new Tenant("TALVEG", esPlataforma: true);
        var tenantCliente = new Tenant("Tenant beneficiario");
        contexto.Tenants.AddRange(tenantPlataforma, tenantCliente);

        var delegacion = DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantCliente.Id);
        contexto.DelegacionesTenant.Add(delegacion);
        await contexto.SaveChangesAsync();

        _tenantClienteId = tenantCliente.Id;

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
        var generador = new GenerarClaveApiCommandHandler(
            new DelegacionTenantRepository(contexto), new ClaveApiRepository(contexto), contexto,
            new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: tenantPlataforma.Id), contexto);

        var generada = await generador.Handle(
            new GenerarClaveApiCommand(delegacion.Id, "Integración externa de prueba"), CancellationToken.None);

        generada.EsExitoso.Should().BeTrue("sin una clave emitida no hay nada que autenticar");
        _claveEnClaro = generada.Valor.ClaveEnClaro;
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
