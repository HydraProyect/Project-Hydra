using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Tests;

/// <summary>
/// ZAP autenticado (run 41250723975, ASVS 4.0.3 V7.4.1 y V13.2.3) midió 500 en los POST de
/// formulario de /cuenta/*. Aquí se reproduce el orden REAL del pipeline de Program.cs
/// (UseExceptionHandler, UseStatusCodePagesWithReExecute, autenticación, autorización,
/// UseAntiforgery) con endpoints de formulario del mismo tipo y páginas Blazor
/// /not-found y /Error: una petición sin token válido o mal formada ha de recibir un 4xx
/// sin detalle, nunca un 500.
/// </summary>
public class RechazoDeAntiforgeryEnPostDeCuentaTests
{
    private const string Ruta = "/cuenta/prueba";
    private const string RutaSinFormulario = "/cuenta/prueba-sin-campos";
    private const string RutaConGuid = "/cuenta/prueba-guid";
    private const string RutaQueRechaza = "/cuenta/prueba-rechaza";

    public static TheoryData<string> Rutas => new() { Ruta, RutaSinFormulario };

    /// <summary>
    /// Control positivo del instrumento: sin el middleware el mismo arnés reproduce el 500 medido por ZAP. Si el
    /// framework lo corrigiera algún día, este caso avisará de que el middleware ya no hace falta.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Control_sin_la_correccion_el_rechazo_acaba_en_500(string ruta)
    {
        await using var app = await ArrancarAsync(conCorreccion: false);
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, new FormUrlEncodedContent([]));
        respuesta.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_cookie_y_sin_token_responde_400_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, new FormUrlEncodedContent([]));
        await SinDetalle(respuesta, HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_token_que_no_corresponde_a_la_cookie_responde_400_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = "no-es-un-token" }));
        await SinDetalle(respuesta, HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Sin_cuerpo_responde_4xx_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, null);
        await SinDetalle(respuesta);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_cuerpo_JSON_en_vez_de_formulario_responde_4xx_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, new StringContent("{\"a\":1}", Encoding.UTF8, "application/json"));
        await SinDetalle(respuesta);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_multipart_mal_formado_responde_4xx_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        var contenido = new StringContent("--x\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\nvalor sin cierre", Encoding.UTF8);
        contenido.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=x");
        using var respuesta = await cliente.PostAsync(ruta, contenido);
        await SinDetalle(respuesta);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_formulario_urlencoded_de_valores_ilegibles_responde_4xx_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        var contenido = new StringContent("a=%ZZ&b=%", Encoding.UTF8, "application/x-www-form-urlencoded");
        using var respuesta = await cliente.PostAsync(ruta, contenido);
        await SinDetalle(respuesta);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_mas_campos_que_el_limite_del_formulario_responde_4xx_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        await cliente.GetStringAsync("/token");
        var campos = string.Join("&", Enumerable.Range(0, 1500).Select(i => $"c{i}=v"));
        using var respuesta = await cliente.PostAsync(ruta, new StringContent(campos, Encoding.UTF8, "application/x-www-form-urlencoded"));
        await SinDetalle(respuesta);
    }

    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task Con_cookie_de_antiforgery_corrupta_responde_4xx_sin_detalle(string ruta)
    {
        await using var app = await ArrancarAsync();
        var galletas = new System.Net.CookieContainer();
        using var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = galletas })
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };
        var token = await cliente.GetStringAsync("/token");
        var antiforgery = galletas.GetCookies(cliente.BaseAddress!).Cast<System.Net.Cookie>()
            .Single(c => c.Name.StartsWith(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        galletas.Add(cliente.BaseAddress!, new System.Net.Cookie(antiforgery.Name, "CfDJ8-corrupta", "/", cliente.BaseAddress!.Host));
        using var respuesta = await cliente.PostAsync(ruta, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        await SinDetalle(respuesta, HttpStatusCode.BadRequest);
    }
    [Theory]
    [InlineData(RutaConGuid, "id=no-es-un-guid")]
    [InlineData(RutaConGuid, "")]
    [InlineData(RutaQueRechaza, "campo=x")]
    public async Task Con_token_valido_y_valor_rechazado_por_el_endpoint_responde_4xx_sin_detalle(string ruta, string campos)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        var token = await cliente.GetStringAsync("/token");
        var cuerpo = "__RequestVerificationToken=" + Uri.EscapeDataString(token) + (campos.Length == 0 ? "" : "&" + campos);
        using var respuesta = await cliente.PostAsync(ruta, new StringContent(cuerpo, Encoding.UTF8, "application/x-www-form-urlencoded"));
        await SinDetalle(respuesta);
    }
    [Theory]
    [MemberData(nameof(Rutas))]
    public async Task El_token_valido_sigue_aceptandose(string ruta)
    {
        await using var app = await ArrancarAsync();
        using var cliente = Cliente(app);
        var token = await cliente.GetStringAsync("/token");
        using var respuesta = await cliente.PostAsync(ruta, new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = token, ["campo"] = "valor" }));
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK, "la validación de antiforgery no se debilita: el token bueno sigue entrando");
    }

    private static async Task SinDetalle(HttpResponseMessage respuesta, HttpStatusCode? esperado = null)
    {
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        var estado = (int)respuesta.StatusCode;
        estado.Should().BeInRange(400, 499, "un rechazo de la petición no es un fallo del servidor; cuerpo: " + cuerpo + " REGISTRO: " + string.Join(" // ", ProveedorDiagnostico.Lineas.Where(l => !l.StartsWith("Information Executing") && !l.StartsWith("Information Executed") && !l.StartsWith("Information Request starting")).Where(l => l.StartsWith("Error") || l.StartsWith("Warning") || l.Contains("Exception")).Take(8)));
        if (esperado is { } concreto) respuesta.StatusCode.Should().Be(concreto);
        cuerpo.Should().NotContain("Exception").And.NotContain("   at ").And.NotContain("Stack");
    }

    private static HttpClient Cliente(WebApplication app) => new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
    };

    private static async Task<WebApplication> ArrancarAsync(bool conCorreccion = true)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddLogging();
        builder.Logging.AddProvider(ProveedorDiagnostico.Instancia);
        builder.Services.AddDataProtection();
        builder.Services.AddAntiforgery();
        builder.Services.AddRazorComponents();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization(o => o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        var app = builder.Build();

        // Mismo orden que Program.cs.
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.UseAuthentication();
        app.UseAuthorization();
        if (conCorreccion) app.UseRechazoDeAntiforgeryReejecutado();
        app.UseAntiforgery();

        app.MapGet("/token", async (HttpContext contexto, IAntiforgery antiforgery) =>
        {
            contexto.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], CookieAuthenticationDefaults.AuthenticationScheme));
            await contexto.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, contexto.User);
            return antiforgery.GetAndStoreTokens(contexto).RequestToken!;
        }).AllowAnonymous();
        app.MapPost(Ruta, ([FromForm] string? campo) => Results.Ok(campo));
        app.MapPost(RutaConGuid, ([FromForm] Guid id) => Results.Ok(id));
        app.MapPost(RutaQueRechaza, ([FromForm] string? campo) => Results.BadRequest());
        app.MapPost(RutaSinFormulario, (IFormCollection formulario) => Results.Ok(formulario.Count));
        app.MapRazorComponents<RaizPrueba>();
        await app.StartAsync();
        return app;
    }

    private sealed class ProveedorDiagnostico : ILoggerProvider, ILogger
    {
        public static readonly ProveedorDiagnostico Instancia = new();
        public static readonly System.Collections.Concurrent.ConcurrentQueue<string> Lineas = new();
        public ILogger CreateLogger(string categoria) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState estado) where TState : notnull => null;
        public bool IsEnabled(LogLevel nivel) => nivel >= LogLevel.Warning;
        public void Log<TState>(LogLevel nivel, EventId id, TState estado, Exception? excepcion, Func<TState, Exception?, string> formateo)
            => Lineas.Enqueue(nivel + " " + formateo(estado, excepcion) + " " + excepcion);
    }

    private sealed class RaizPrueba : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b)
        {
            b.OpenComponent<Router>(0);
            b.AddComponentParameter(1, nameof(Router.AppAssembly), typeof(RaizPrueba).Assembly);
            b.AddComponentParameter(2, nameof(Router.Found), (RenderFragment<RouteData>)(datos => c =>
            {
                c.OpenComponent<RouteView>(0);
                c.AddComponentParameter(1, nameof(RouteView.RouteData), datos);
                c.CloseComponent();
            }));
            b.CloseComponent();
        }
    }

    [Microsoft.AspNetCore.Components.Route("/not-found")]
    [AllowAnonymous]
    public sealed class PaginaNoEncontradaPrueba : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddContent(0, "pagina no encontrada");
    }

    [Microsoft.AspNetCore.Components.Route("/Error")]
    [AllowAnonymous]
    public sealed class PaginaErrorPrueba : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder b) => b.AddContent(0, "error generico");
    }
}
