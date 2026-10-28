using System.Net;
using System.Threading.RateLimiting;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// S-A-6: el limitador de la API pública corre antes de <c>UseAuthorization</c> y el esquema por
/// defecto es la cookie, así que el principal que veía no era el de la clave y todo el tráfico con
/// clave compartía un único cupo "sin-tenant": un anónimo con cabeceras basura podía dar 429 a las
/// integraciones de todos los Tenants. Aquí se monta el limitador real con las mismas funciones de
/// partición que usa <c>Program.cs</c> (<see cref="LimitadorApiPublica"/>) sobre Kestrel en un puerto
/// efímero (el proyecto no referencia TestHost). Que <c>Program.cs</c> las use lo vigila
/// <c>LimitadorApiPublicaRegistradoEnProgramTests</c> en Architecture.Tests. Límite del instrumento:
/// no hay autenticación en este pipeline; el limitador reparte el cupo antes de autenticar, que es
/// justo lo que se prueba.
/// </summary>
public class LimitadorApiPublicaTests
{
    private const int CupoPorClave = 3;

    [Fact]
    public async Task Dos_claves_distintas_no_comparten_cupo()
    {
        await using var sitio = await Sitio.IniciarAsync(CupoPorClave, limitePorIp: 100);

        for (var i = 0; i < CupoPorClave; i++)
            (await sitio.PedirAsync("/api/v1/ping", "ApiKey clave-del-tenant-a")).Should().Be(HttpStatusCode.OK);

        (await sitio.PedirAsync("/api/v1/ping", "ApiKey clave-del-tenant-a"))
            .Should().Be(HttpStatusCode.TooManyRequests, "control positivo: el cupo de la clave A se agotó");

        (await sitio.PedirAsync("/api/v1/ping", "ApiKey clave-del-tenant-b"))
            .Should().Be(HttpStatusCode.OK, "la clave B tiene su propio cupo");
    }

    [Fact]
    public async Task Un_anonimo_con_cabeceras_basura_no_agota_el_cupo_de_una_clave_valida()
    {
        // El techo por IP es holgado a propósito: el middleware reintenta el cupo global cuando la política
        // rechaza, así que cada petición rechazada gasta dos permisos de IP y 60 peticiones pueden costar 120.
        await using var sitio = await Sitio.IniciarAsync(CupoPorClave, limitePorIp: 1000);

        var basura = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
            basura.Add(await sitio.PedirAsync("/api/v1/ping", "ApiKey basura"));
        for (var i = 0; i < 20; i++)
            basura.Add(await sitio.PedirAsync("/api/v1/ping", cabeceraAuthorization: null));
        for (var i = 0; i < 20; i++)
            basura.Add(await sitio.PedirAsync("/api/v1/ping", "Bearer abc.def.ghi"));

        basura.Should().Contain(HttpStatusCode.TooManyRequests, "control positivo: la basura sí agota su propio cupo");
        for (var i = 0; i < CupoPorClave; i++)
            (await sitio.PedirAsync("/api/v1/ping", "ApiKey clave-del-tenant-a"))
                .Should().Be(HttpStatusCode.OK, "el cupo de una clave no lo consume nadie más");
    }

    [Fact]
    public async Task Rotar_claves_inventadas_no_da_un_cupo_nuevo_sin_limite_por_la_misma_IP()
    {
        await using var sitio = await Sitio.IniciarAsync(CupoPorClave, limitePorIp: 5);

        var estados = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
            estados.Add(await sitio.PedirAsync("/api/v1/ping", $"ApiKey inventada-{i}"));

        estados.Take(5).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        estados.Skip(5).Should().AllBeEquivalentTo(HttpStatusCode.TooManyRequests,
            "cada clave inventada tiene cupo propio, pero la IP tiene un techo");
    }

    [Fact]
    public async Task Variar_espacios_o_mayusculas_de_la_cabecera_no_multiplica_el_cupo_de_una_clave()
    {
        await using var sitio = await Sitio.IniciarAsync(CupoPorClave, limitePorIp: 100);

        (await sitio.PedirAsync("/api/v1/ping", "ApiKey la-clave")).Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/api/v1/ping", "apikey   la-clave  ")).Should().Be(HttpStatusCode.OK);
        (await sitio.PedirAsync("/api/v1/ping", "APIKEY la-clave")).Should().Be(HttpStatusCode.OK);

        (await sitio.PedirAsync("/api/v1/ping", "ApiKey  la-clave"))
            .Should().Be(HttpStatusCode.TooManyRequests, "el handler resuelve las cuatro variantes a la misma clave");
    }

    [Fact]
    public async Task El_techo_por_IP_no_alcanza_a_las_rutas_fuera_de_la_API()
    {
        await using var sitio = await Sitio.IniciarAsync(CupoPorClave, limitePorIp: 2);

        for (var i = 0; i < 10; i++)
            (await sitio.PedirAsync("/fuera", cabeceraAuthorization: null)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public void La_particion_lleva_una_huella_de_la_clave_y_nunca_la_clave_en_claro()
    {
        const string clave = "tlv_clave-secreta-de-prueba";
        var contexto = new DefaultHttpContext();
        contexto.Request.Headers.Authorization = $"ApiKey {clave}";

        var particion = LimitadorApiPublica.ParticionPorLlamante(contexto, CupoPorClave).PartitionKey;

        particion.Should().StartWith("clave:")
            .And.NotContain(clave)
            .And.NotContain(CabeceraClaveApi.HashDeClave(clave), "tampoco el hash que se persiste en base");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer abc")]
    [InlineData("ApiKey ")]
    [InlineData("ApiKey")]
    public void Sin_clave_presentada_la_particion_es_la_IP_del_cliente(string? cabecera)
    {
        var contexto = new DefaultHttpContext();
        contexto.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        if (cabecera is not null)
            contexto.Request.Headers.Authorization = cabecera;

        LimitadorApiPublica.ParticionPorLlamante(contexto, CupoPorClave).PartitionKey
            .Should().Be("ip:203.0.113.9");
    }

    [Theory]
    [InlineData("/api/v1/clientes", true)]
    [InlineData("/api/v1", true)]
    [InlineData("/api/v2/clientes", false)]
    [InlineData("/api/v10/clientes", false)]
    [InlineData("/cuenta/iniciar-sesion", false)]
    public void El_techo_global_por_IP_solo_aplica_bajo_api_v1(string ruta, bool aplica)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Path = ruta;

        LimitadorApiPublica.ParticionGlobal(contexto, 10).HasValue.Should().Be(aplica);
    }

    [Theory]
    [InlineData("ApiKey abc", "abc")]
    [InlineData("apikey   abc  ", "abc")]
    [InlineData("ApiKey ", "")]
    [InlineData("Bearer abc", null)]
    [InlineData("ApiKey", null)]
    [InlineData(null, null)]
    public void ExtraerClave_distingue_otro_esquema_de_clave_vacia(string? cabecera, string? esperada) =>
        CabeceraClaveApi.ExtraerClave(cabecera).Should().Be(esperada);

    private sealed class Sitio : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _cliente;

        private Sitio(WebApplication app, HttpClient cliente)
        {
            _app = app;
            _cliente = cliente;
        }

        public static async Task<Sitio> IniciarAsync(int limitePorClave, int limitePorIp)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddRateLimiter(opciones =>
            {
                opciones.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
                opciones.AddPolicy(LimitadorApiPublica.Politica, contexto =>
                    LimitadorApiPublica.ParticionPorLlamante(contexto, limitePorClave));
                opciones.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(contexto =>
                    LimitadorApiPublica.ParticionGlobal(contexto, limitePorIp) is { } particion
                        ? particion
                        : RateLimitPartition.GetNoLimiter("sin-limite"));
            });

            var app = builder.Build();
            app.UseRateLimiter();
            app.MapGroup("/api/v1").RequireRateLimiting(LimitadorApiPublica.Politica).MapGet("/ping", () => "ok");
            app.MapGet("/fuera", () => "ok");
            await app.StartAsync();

            var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            return new Sitio(app, new HttpClient { BaseAddress = new Uri(direccion) });
        }

        public async Task<HttpStatusCode> PedirAsync(string ruta, string? cabeceraAuthorization)
        {
            using var peticion = new HttpRequestMessage(HttpMethod.Get, ruta);
            if (cabeceraAuthorization is not null)
                peticion.Headers.TryAddWithoutValidation("Authorization", cabeceraAuthorization);
            using var respuesta = await _cliente.SendAsync(peticion);
            return respuesta.StatusCode;
        }

        public async ValueTask DisposeAsync()
        {
            _cliente.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
