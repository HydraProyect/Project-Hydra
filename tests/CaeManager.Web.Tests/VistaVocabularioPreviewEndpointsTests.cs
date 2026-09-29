using System.Net;
using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
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
/// La vista previa de vocabulario vuelve con una redirección LOCAL y sin la marca de un solo uso del
/// login: el selector toma como returnUrl la URL con la que se creó el circuito (tras iniciar sesión,
/// <c>/?desde=login</c>) y devolver al usuario ahí reactivaría el aterrizaje D-2.
/// </summary>
public class VistaVocabularioPreviewEndpointsTests
{
    [Theory]
    [InlineData("/?desde=login", "/")]
    [InlineData("/empresas?x=1&desde=login", "/empresas?x=1")]
    [InlineData("/empresas", "/empresas")]
    [InlineData("//ajeno.example/", "/")]
    public async Task Cambiar_la_vista_de_vocabulario_vuelve_sin_la_marca_del_login_y_siempre_a_una_ruta_local(
        string returnUrl, string destinoEsperado)
    {
        await using var app = await ArrancarAsync();
        using var cliente = CrearCliente(app);

        var respuesta = await cliente.PostAsync("/cuenta/vista-vocabulario", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["perfil"] = "", ["returnUrl"] = returnUrl }));

        respuesta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        respuesta.Headers.Location!.OriginalString.Should().Be(destinoEsperado);
    }

    private static HttpClient CrearCliente(WebApplication app)
    {
        var direccion = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(direccion) };
    }

    private static async Task<WebApplication> ArrancarAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection();
        // La validación del token es de Program.cs (UseAntiforgery) y tiene su propio test; aquí solo
        // se mide el contrato de transporte del endpoint, así que el guardián de prueba deja pasar.
        builder.Services.AddSingleton<IAntiforgery>(new AntiforgeryQueDejaPasar());

        var app = builder.Build();
        // El endpoint exige el rol Administrador: se inyecta la identidad sin montar la autenticación.
        app.Use(async (contexto, siguiente) =>
        {
            contexto.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Role, Roles.Administrador)], "prueba"));
            await siguiente();
        });
        app.UseAntiforgery();
        app.MapVistaVocabularioPreviewEndpoints();
        await app.StartAsync();
        return app;
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
