using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// El escenario que dio origen a este middleware: <b>contraseña temporal sin
/// cambiar, descargando un PDF</b>.
///
/// <para>
/// La contraseña temporal se envía por correo, no caduca por sí sola, y su
/// obligación de cambio vivía únicamente en <c>MainLayout</c> — una pantalla,
/// no un control de acceso. Quien iniciara sesión con ella tenía una cookie
/// válida y podía llamar a <c>GET /documentos/{id}/archivo</c>, que no declara
/// autorización propia y por tanto solo exige el <c>FallbackPolicy</c>
/// (<c>RequireAuthenticatedUser</c>). Datos de salud sin haber activado la
/// cuenta. Lo mismo para un Administrador sin la 2FA que su rol exige.
/// </para>
/// </summary>
public class CuentaAMedioActivarSinAccesoMiddlewareTests
{
    [Fact]
    public async Task Una_cuenta_a_medio_activar_no_descarga_un_pdf()
    {
        var contexto = ContextoCon("/documentos/8f3c1e2a-0000-0000-0000-000000000001/archivo", requiereActivacion: true);

        var siguienteFueLlamado = await EjecutarAsync(contexto);

        siguienteFueLlamado.Should().BeFalse("la petición no puede llegar al endpoint que sirve el archivo");
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden,
            "a una descarga se le contesta 403; redirigirla produciría un PDF corrupto en vez de un error visible");
    }

    [Fact]
    public async Task Una_navegacion_se_redirige_a_la_pantalla_que_resuelve_el_problema()
    {
        // Las dos cosas que definen una navegación de nivel superior. El método
        // hay que ponerlo explícitamente: DefaultHttpContext no trae ninguno, y
        // sin él esto se clasificaría como "no es navegación" y contestaría 403
        // — que es justo lo que pasó la primera vez que corrió este test.
        var contexto = ContextoCon("/documentos", requiereActivacion: true);
        contexto.Request.Method = HttpMethods.Get;
        contexto.Request.Headers.Accept = "text/html,application/xhtml+xml";

        var siguienteFueLlamado = await EjecutarAsync(contexto);

        siguienteFueLlamado.Should().BeFalse();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        contexto.Response.Headers.Location.ToString().Should().Be("/cuenta/cambiar-contrasena");
    }

    [Theory]
    [InlineData("/cuenta/cambiar-contrasena")]
    [InlineData("/cuenta/configurar-2fa")]
    [InlineData("/cuenta/cerrar-sesion")]
    [InlineData("/cuenta/verificar-2fa")]
    public async Task Las_pantallas_que_activan_la_cuenta_siguen_alcanzables(string ruta)
    {
        // Sin esto el middleware sería un cepo: la cuenta no tendría por dónde
        // salir del estado que la bloquea.
        var contexto = ContextoCon(ruta, requiereActivacion: true);

        (await EjecutarAsync(contexto)).Should().BeTrue();
    }

    [Theory]
    [InlineData("/_blazor")]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/_content/algo.css")]
    public async Task La_infraestructura_de_Blazor_no_se_corta(string ruta)
    {
        // Cortar /_blazor tiraría el circuito en vez de redirigir, y el usuario
        // vería una página rota en lugar del formulario que tiene que rellenar.
        var contexto = ContextoCon(ruta, requiereActivacion: true);

        (await EjecutarAsync(contexto)).Should().BeTrue();
    }

    [Fact]
    public async Task Una_cuenta_activada_no_paga_nada()
    {
        var contexto = ContextoCon("/documentos/8f3c1e2a-0000-0000-0000-000000000001/archivo", requiereActivacion: false);

        (await EjecutarAsync(contexto)).Should().BeTrue();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Un_anonimo_no_se_bloquea_aqui()
    {
        // Quien no ha entrado lo resuelve el FallbackPolicy, no este
        // middleware. Bloquearlo aquí daría 403 donde debe haber una
        // redirección al login.
        var contexto = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        contexto.Request.Path = "/documentos";

        (await EjecutarAsync(contexto)).Should().BeTrue();
    }

    [Fact]
    public async Task El_claim_solo_bloquea_con_el_valor_exacto()
    {
        // HasClaim compara valor: un claim presente con cualquier otro texto no
        // significa "activada", pero tampoco puede significar "bloqueada" por
        // accidente. Se fija el contrato para que un cambio de serialización se
        // note aquí y no en producción.
        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
                new Claim(TenantClaimsPrincipalFactory.TipoClaimRequiereActivacion, "False"),
            ], "prueba")),
        };
        contexto.Request.Path = "/documentos";

        (await EjecutarAsync(contexto)).Should().BeTrue();
    }

    private static async Task<bool> EjecutarAsync(HttpContext contexto)
    {
        var siguienteFueLlamado = false;
        var middleware = new CuentaAMedioActivarSinAccesoMiddleware(_ =>
        {
            siguienteFueLlamado = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(contexto);
        return siguienteFueLlamado;
    }

    private static DefaultHttpContext ContextoCon(string ruta, bool requiereActivacion)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())];

        if (requiereActivacion)
            claims.Add(new Claim(TenantClaimsPrincipalFactory.TipoClaimRequiereActivacion, "true"));

        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba")),
        };

        contexto.Request.Path = ruta;
        return contexto;
    }
}
