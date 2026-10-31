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
using Microsoft.AspNetCore.DataProtection;
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

    private Escenario Escena(string ruta = "/extension/acreditaciones-pendientes", string autorizacion = TokenDePrueba, bool autenticado = true)
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
