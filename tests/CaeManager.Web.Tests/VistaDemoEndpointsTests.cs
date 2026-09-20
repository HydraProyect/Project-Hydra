using System.Net;
using CaeManager.Application.Common;
using CaeManager.Application.VistaDemo;
using CaeManager.Web.Features.Tenants;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CaeManager.Web.Tests;

/// <summary>
/// El cambio de vista de demo tiene que ser una ida y vuelta HTTP completa que termina en una
/// redirección LOCAL. Esa es la propiedad de la que depende la lente: el alcance de datos se
/// memoiza por circuito de Blazor (<c>AlcanceDatosService</c>) y solo un circuito nuevo lo recalcula.
/// Si alguien convirtiera el cambio en algo que se resuelve dentro del circuito, la lente dejaría de
/// aplicarse sin avisar; este test se pone rojo antes. No prueba qué vistas valen (eso es
/// VistaDemoLenteTests) sino el contrato de transporte del endpoint.
/// </summary>
public class VistaDemoEndpointsTests
{
    private static readonly Guid Usuario = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Theory]
    [InlineData("direccion", "/empresas", "/empresas")]
    [InlineData("coordinador", "/", "/")]
    [InlineData("coordinador", "https://ajeno.example/", "/")]
    [InlineData("coordinador", "//ajeno.example/", "/")]
    public async Task Cambiar_de_vista_responde_con_una_redireccion_local_nunca_un_resultado_dentro_del_circuito(
        string opcion, string returnUrl, string destinoEsperado)
    {
        await using var app = await ArrancarAsync(disponible: true);
        using var cliente = CrearCliente(app);

        var respuesta = await cliente.PostAsync("/cuenta/vista-demo", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["opcion"] = opcion, ["returnUrl"] = returnUrl }));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Redirect, "un cambio que no recarga no recalcula el alcance memoizado");
        respuesta.Headers.Location!.OriginalString.Should().Be(destinoEsperado);
    }

    [Fact]
    public async Task Si_la_cuenta_no_puede_usar_la_lente_no_hay_redireccion_de_exito_ni_cookie()
    {
        await using var app = await ArrancarAsync(disponible: false);
        using var cliente = CrearCliente(app);

        var respuesta = await cliente.PostAsync("/cuenta/vista-demo", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["opcion"] = "coordinador", ["returnUrl"] = "/" }));

        respuesta.StatusCode.Should().NotBe(HttpStatusCode.Redirect);
        respuesta.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    private static HttpClient CrearCliente(WebApplication app)
    {
        var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(direccion) };
    }

    private static async Task<WebApplication> ArrancarAsync(bool disponible)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection();
        // La validación del token es de Program.cs (UseAntiforgery) y tiene su propio test; aquí solo
        // se mide el contrato de transporte del endpoint, así que el guardián de prueba deja pasar.
        builder.Services.AddSingleton<IAntiforgery>(new AntiforgeryQueDejaPasar());
        builder.Services.AddSingleton<IVistaDemoActual>(new VistaDemoFalsa(disponible));
        builder.Services.AddSingleton<ICurrentUserService>(new UsuarioFalso());

        var app = builder.Build();
        app.UseAntiforgery();
        app.MapVistaDemoEndpoints();
        await app.StartAsync();
        return app;
    }

    private sealed class VistaDemoFalsa(bool disponible) : IVistaDemoActual
    {
        public Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => Task.FromResult(disponible);

        public Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<VistaDemoEfectiva?>(null);

        public Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GestorDeVistaDemo>>([]);

        public Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }

    private sealed class UsuarioFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Usuario);

        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);

        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);

        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class AntiforgeryQueDejaPasar : IAntiforgery
    {
        private static readonly AntiforgeryTokenSet Vacio = new(null, null, "f", "h");

        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => Vacio;

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => Vacio;

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext)
        {
        }

        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
    }
}
