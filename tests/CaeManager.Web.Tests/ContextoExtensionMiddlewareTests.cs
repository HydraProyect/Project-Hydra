using System.Net;
using System.Reflection;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Extension;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La parte de <see cref="ContextoExtensionMiddleware"/> que no necesita base: cuándo actúa, que la cookie no entra y qué
/// se rechaza antes de consultar nada. Con la base y bajo RLS, <c>ExtensionTenantBeneficiarioBajoRlsTests</c>.
/// </summary>
public class ContextoExtensionMiddlewareTests
{
    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid TenantOrigen = Guid.NewGuid();
    private static readonly Guid OtroTenant = Guid.NewGuid();
    private const string TokenDePrueba = "Extension token-de-prueba";
    private readonly EphemeralDataProtectionProvider _proteccion = new();

    [Theory]
    [InlineData("/documentos", TokenDePrueba)]
    [InlineData("/extensiones", TokenDePrueba)]
    [InlineData("/extension/acreditaciones-pendientes", "")]
    [InlineData("/extension/acreditaciones-pendientes", "Bearer otro")]
    public async Task Fuera_de_las_rutas_de_la_extension_o_sin_su_token_no_toca_nada(string ruta, string autorizacion)
    {
        var escena = Escena(ruta, autorizacion);
        var sesion = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "cookie"));
        escena.Contexto.User = sesion;

        await escena.EjecutarAsync();

        escena.Paso.Should().BeTrue();
        escena.Contexto.User.Should().BeSameAs(sesion);
        ContextoExtension.Es(escena.Contexto).Should().BeFalse();
        escena.Autenticaciones.Should().Be(0);
    }

    [Theory]
    [InlineData("/extension/acreditaciones-pendientes", TokenDePrueba, true)]
    [InlineData("/EXTENSION/acreditaciones-pendientes", TokenDePrueba, true)]
    [InlineData("/extension/tenants-beneficiarios/", TokenDePrueba, true)]
    [InlineData("/extension", TokenDePrueba, true)]
    [InlineData("/extension/acreditaciones-pendientes", "extension token-de-prueba", true)]
    [InlineData("/extensiones/acreditaciones-pendientes", TokenDePrueba, false)]
    [InlineData("/cuenta/extension/token", TokenDePrueba, false)]
    [InlineData("/extension/acreditaciones-pendientes", "Extensiontoken-de-prueba", false)]
    public void Aplica_con_el_mismo_criterio_que_el_enrutado_y_que_el_esquema(string ruta, string autorizacion, bool esperado)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Path = ruta;
        contexto.Request.Headers.Authorization = autorizacion;

        ContextoExtension.Aplica(contexto.Request).Should().Be(esperado);
    }

    [Fact]
    public async Task Un_token_rechazado_responde_401_y_no_cae_a_la_cookie()
    {
        var escena = Escena(autenticado: false);
        escena.Contexto.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], "cookie"));

        await escena.EjecutarAsync();

        escena.Paso.Should().BeFalse();
        escena.Contexto.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        escena.Contexto.User.Identity!.IsAuthenticated.Should().BeFalse("la identidad de la cookie no sigue en la petición");
    }

    [Fact]
    public async Task Con_token_y_sin_cabecera_se_ignora_la_cookie_de_seleccion_y_se_opera_en_origen()
    {
        var escena = Escena();
        var sesionPrivilegiada = Guid.NewGuid();
        escena.Contexto.Request.Headers.Cookie =
            $"{ClienteActivoSeleccionado.NombreCookie}={ClienteActivoSeleccionado.Proteger(_proteccion, Usuario, Guid.NewGuid(), Guid.NewGuid(), sesionPrivilegiada)}";

        await escena.EjecutarAsync();

        escena.Paso.Should().BeTrue();
        ContextoExtension.Es(escena.Contexto).Should().BeTrue();
        escena.Contexto.User.Identity!.AuthenticationType.Should().Be(ExtensionAuthenticationSchemeOptions.NombreEsquema);
        escena.Seleccion.TenantIdSeleccionado.Should().BeNull();
        escena.Seleccion.AsignacionOperacionIdSeleccionada.Should().BeNull();
        escena.Seleccion.SesionPrivilegiadaIdSeleccionada.Should().BeNull();
    }

    [Fact]
    public async Task Pedir_el_Tenant_de_origen_no_abre_ninguna_seleccion()
    {
        var escena = Escena();
        escena.Contexto.Request.Headers[ContextoExtension.CabeceraTenant] = TenantOrigen.ToString();

        await escena.EjecutarAsync();

        escena.Paso.Should().BeTrue();
        escena.Seleccion.TenantIdSeleccionado.Should().BeNull();
    }

    [Fact]
    public async Task Sin_cabecera_la_respuesta_confirma_el_Tenant_de_origen()
    {
        var escena = Escena();

        await escena.EjecutarAsync();

        escena.TenantConfirmado.Should().Be(TenantOrigen.ToString("D"));
    }

    [Fact]
    public async Task La_confirmacion_es_el_Tenant_efectivo_y_no_el_eco_de_la_solicitud()
    {
        var escena = Escena(tenantEfectivo: OtroTenant);
        escena.Contexto.Request.Headers[ContextoExtension.CabeceraTenant] = TenantOrigen.ToString();

        await escena.EjecutarAsync();

        escena.Paso.Should().BeTrue();
        escena.TenantConfirmado.Should().Be(OtroTenant.ToString("D"));
    }

    [Fact]
    public async Task Sin_Tenant_efectivo_no_se_confirma_ninguno()
    {
        var escena = Escena(tenantEfectivo: null);

        await escena.EjecutarAsync();

        escena.TenantConfirmado.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_rechazo_no_confirma_ningun_Tenant()
    {
        var escena = Escena();
        escena.Contexto.Request.Headers[ContextoExtension.CabeceraTenant] = "no-es-un-guid";

        await escena.EjecutarAsync();

        escena.DebeHaberRechazado();
        escena.TenantConfirmado.Should().BeEmpty();
    }

    [Fact]
    public async Task Fuera_del_contexto_de_la_extension_no_se_confirma_nada()
    {
        var escena = Escena("/documentos");

        await escena.EjecutarAsync();

        escena.Paso.Should().BeTrue();
        escena.TenantConfirmado.Should().BeEmpty();
    }

    /// <summary>
    /// Un 404 sin cuerpo de la entrega lo reejecuta <c>UseStatusCodePagesWithReExecute</c>, con contenedor nuevo. Si ahí se
    /// perdiera la confirmación, la extensión tomaría un 404 legítimo por un Tenant sin confirmar y no soltaría el envío.
    /// </summary>
    [Fact]
    public async Task La_confirmacion_sobrevive_a_la_reejecucion_de_un_404_sin_cuerpo()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<ITenantActual>(new TenantEfectivoDeLaPrueba(OtroTenant));
        await using var app = builder.Build();
        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
        app.Use((contexto, siguiente) =>
        {
            if (contexto.Request.Path.StartsWithSegments("/extension"))
                ContextoExtension.ConfirmarTenantEfectivo(contexto);
            return siguiente();
        });
        app.MapGet("/extension/archivo", () => Results.NotFound());
        app.MapGet("/not-found", () => Results.Text("página de error"));
        await app.StartAsync();
        using var cliente = new HttpClient
        {
            BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()),
        };

        using var respuesta = await cliente.GetAsync("/extension/archivo");

        respuesta.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await respuesta.Content.ReadAsStringAsync()).Should().Be("página de error", "si no, no se habría reejecutado");
        respuesta.Headers.GetValues(ContextoExtension.CabeceraTenantEfectivo).Should().Equal(OtroTenant.ToString("D"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("aaaaaaaa-1111-1111-1111-111111111111,bbbbbbbb-2222-2222-2222-222222222222")]
    public async Task Una_solicitud_mal_formada_se_rechaza_en_cerrado(string valor)
    {
        var escena = Escena();
        escena.Contexto.Request.Headers[ContextoExtension.CabeceraTenant] = valor;

        await escena.EjecutarAsync();

        escena.DebeHaberRechazado();
    }

    [Fact]
    public async Task Dos_cabeceras_de_seleccion_se_rechazan_aunque_cada_una_sea_valida()
    {
        var escena = Escena();
        escena.Contexto.Request.Headers[ContextoExtension.CabeceraTenant] =
            new Microsoft.Extensions.Primitives.StringValues([TenantOrigen.ToString(), Guid.NewGuid().ToString()]);

        await escena.EjecutarAsync();

        escena.DebeHaberRechazado();
    }

    [Fact]
    public async Task El_evaluador_no_vuelve_a_autenticar_una_peticion_ya_resuelta()
    {
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([], ExtensionAuthenticationSchemeOptions.NombreEsquema)),
        };
        ContextoExtension.Marcar(contexto);
        var politica = new AuthorizationPolicyBuilder(ExtensionAuthenticationSchemeOptions.NombreEsquema, "Identity.Application")
            .RequireAuthenticatedUser().Build();

        var resultado = await new EvaluadorPoliticaExtension(null!).AuthenticateAsync(politica, contexto);

        resultado.Succeeded.Should().BeTrue();
        resultado.Principal.Should().BeSameAs(contexto.User, "ni otra identidad ni una fusión con la cookie");
    }

    [Fact]
    public async Task El_evaluador_deja_a_la_base_cualquier_otra_peticion()
    {
        var contexto = new DefaultHttpContext();
        var politica = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        ContextoExtension.Marcar(contexto);

        var resultado = await new EvaluadorPoliticaExtension(null!).AuthenticateAsync(politica, contexto);

        resultado.Succeeded.Should().BeFalse("una política que no admite el esquema de extensión no se da por resuelta");
    }

    [Fact]
    public void La_lista_de_la_extension_no_ofrece_la_via_heredada()
    {
        var (origen, gestionado, heredado) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        var ofrecidos = TenantsBeneficiariosExtensionEndpoints.Ofrecidos(
        [
            new ClienteAutorizadoDto(origen, "Origen", true, false, default, false),
            new ClienteAutorizadoDto(gestionado, "Por operación", false, true, default, true),
            new ClienteAutorizadoDto(heredado, "Por soporte", false, false, default, false),
        ]);

        ofrecidos.Select(t => t.TenantId).Should().Equal(origen, gestionado);
    }

    private Escenario Escena(string ruta = "/extension/acreditaciones-pendientes", string autorizacion = TokenDePrueba, bool autenticado = true) =>
        Escena(TenantOrigen, ruta, autorizacion, autenticado);

    private Escenario Escena(Guid? tenantEfectivo, string ruta = "/extension/acreditaciones-pendientes", string autorizacion = TokenDePrueba, bool autenticado = true)
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Path = ruta;
        contexto.Response.Body = new MemoryStream();
        if (autorizacion.Length > 0)
            contexto.Request.Headers.Authorization = autorizacion;

        var accesor = new HttpContextAccessor { HttpContext = contexto };
        var seleccion = new ClienteActivoSeleccionado(accesor, _proteccion);
        var autenticacion = new AutenticacionFalsa(autenticado);

        var usuario = DispatchProxy.Create<ICurrentUserService, UsuarioDeLaPrueba>();
        contexto.RequestServices = new ServiceCollection()
            .AddSingleton<IAuthenticationService>(autenticacion)
            .AddSingleton<IClienteActivoSeleccionado>(seleccion)
            .AddSingleton<ITenantActual>(new TenantEfectivoDeLaPrueba(tenantEfectivo))
            .AddSingleton(usuario)
            .BuildServiceProvider();

        return new Escenario(contexto, seleccion, autenticacion);
    }

    private sealed class Escenario(DefaultHttpContext contexto, ClienteActivoSeleccionado seleccion, AutenticacionFalsa autenticacion)
    {
        public DefaultHttpContext Contexto { get; } = contexto;
        public ClienteActivoSeleccionado Seleccion { get; } = seleccion;
        public bool Paso { get; private set; }
        public int Autenticaciones => autenticacion.Llamadas;
        public string TenantConfirmado => Contexto.Response.Headers[ContextoExtension.CabeceraTenantEfectivo].ToString();

        public Task EjecutarAsync() => new ContextoExtensionMiddleware(_ =>
        {
            Paso = true;
            return Task.CompletedTask;
        }).InvokeAsync(Contexto);

        public void DebeHaberRechazado()
        {
            Paso.Should().BeFalse();
            Contexto.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            Contexto.Response.ContentType.Should().StartWith("application/problem+json");
            Contexto.Response.Body.Position = 0;
            new StreamReader(Contexto.Response.Body).ReadToEnd().Should().Contain(ContextoExtension.TituloSeleccionNoAutorizada);
            Seleccion.TenantIdSeleccionado.Should().BeNull();
        }
    }

    private sealed class TenantEfectivoDeLaPrueba(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class AutenticacionFalsa(bool autenticado) : IAuthenticationService
    {
        public int Llamadas { get; private set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            Llamadas++;
            scheme.Should().Be(ExtensionAuthenticationSchemeOptions.NombreEsquema);
            var identidad = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, Usuario.ToString())], scheme);
            return Task.FromResult(autenticado
                ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidad), scheme!))
                : AuthenticateResult.Fail("Token inválido o caducado."));
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }

    /// <summary>Solo responde quién es el usuario y su Tenant de origen: cualquier otra pregunta haría fallar la prueba.</summary>
    public class UsuarioDeLaPrueba : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? metodo, object?[]? argumentos) => metodo?.Name switch
        {
            nameof(ICurrentUserService.ObtenerUsuarioActualIdAsync) => Task.FromResult<Guid?>(Usuario),
            nameof(ICurrentUserService.ObtenerTenantOrigenIdAsync) => Task.FromResult<Guid?>(TenantOrigen),
            _ => throw new NotSupportedException(metodo?.Name),
        };
    }
}
