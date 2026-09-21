using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.StaticAssets;

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

    [Theory]
    [InlineData("/css/tokens.abc123.css")]
    [InlineData("/js/acceso-contrasena.def456.js")]
    [InlineData("/CaeManager.Web.ghi789.styles.css")]
    [InlineData("/favicon.svg")]
    public async Task Los_estaticos_del_propio_flujo_se_sirven_aunque_la_cuenta_este_a_medio_activar(string ruta)
    {
        // Antes eran un 403: la pantalla «Cambiar contraseña» salía sin estilos
        // ni scripts porque su propio CSS/JS pasaba por este middleware. Se
        // reconocen por el endpoint de MapStaticAssets (los nombres llevan
        // huella y ningún prefijo de ruta los cubriría).
        var contexto = ContextoCon(ruta, requiereActivacion: true);
        contexto.SetEndpoint(EndpointDeEstatico(ruta));

        (await EjecutarAsync(contexto)).Should().BeTrue();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task Una_ruta_que_parece_estatica_pero_no_lo_es_sigue_bloqueada()
    {
        // Control negativo: la excepción no puede ser «lo que empiece por /css/»
        // ni «lo que tenga extensión». Sin endpoint de asset (aquí, ninguno; en
        // producción, un endpoint de datos) se aplica la regla de siempre.
        var contexto = ContextoCon("/css/exportacion-de-datos.csv", requiereActivacion: true);

        (await EjecutarAsync(contexto)).Should().BeFalse();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Un_endpoint_que_no_es_un_asset_no_se_libra_por_su_ruta()
    {
        var contexto = ContextoCon("/documentos/8f3c1e2a-0000-0000-0000-000000000001/archivo", requiereActivacion: true);
        contexto.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(), "descarga"));

        (await EjecutarAsync(contexto)).Should().BeFalse();
        contexto.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Con_solo_la_2fa_pendiente_la_navegacion_va_a_configurarla_no_a_cambiar_la_contrasena()
    {
        // Administrador sin 2FA y sin contraseña temporal: mandarlo a «Cambiar
        // contraseña» le pide algo que no debe y no le dice lo que sí.
        var contexto = ContextoCon("/documentos", requiereActivacion: true,
            pendiente: TenantClaimsPrincipalFactory.ActivacionPendienteDosFactores);
        contexto.Request.Method = HttpMethods.Get;
        contexto.Request.Headers.Accept = "text/html";

        await EjecutarAsync(contexto);

        contexto.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        contexto.Response.Headers.Location.ToString().Should().Be("/cuenta/configurar-2fa");
    }

    [Fact]
    public async Task Con_la_contrasena_pendiente_la_navegacion_va_a_cambiarla()
    {
        var contexto = ContextoCon("/documentos", requiereActivacion: true,
            pendiente: TenantClaimsPrincipalFactory.ActivacionPendienteContrasena);
        contexto.Request.Method = HttpMethods.Get;
        contexto.Request.Headers.Accept = "text/html";

        await EjecutarAsync(contexto);

        contexto.Response.Headers.Location.ToString().Should().Be("/cuenta/cambiar-contrasena");
    }

    [Fact]
    public async Task Sin_la_pista_de_destino_se_mantiene_la_contrasena_de_siempre()
    {
        // Cookie emitida antes de existir la pista: el comportamiento previo,
        // que se equivoca hacia seguir exigiendo, nunca hacia dejar pasar.
        var contexto = ContextoCon("/documentos", requiereActivacion: true);
        contexto.Request.Method = HttpMethods.Get;
        contexto.Request.Headers.Accept = "text/html";

        await EjecutarAsync(contexto);

        contexto.Response.Headers.Location.ToString().Should().Be("/cuenta/cambiar-contrasena");
    }

    private static Endpoint EndpointDeEstatico(string ruta) =>
        new(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new StaticAssetDescriptor { Route = ruta.TrimStart('/'), AssetPath = ruta.TrimStart('/') }),
            "asset");

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

    private static DefaultHttpContext ContextoCon(
        string ruta, bool requiereActivacion, string? pendiente = null)
    {
        List<Claim> claims = [new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())];

        if (requiereActivacion)
            claims.Add(new Claim(TenantClaimsPrincipalFactory.TipoClaimRequiereActivacion, "true"));

        if (pendiente is not null)
            claims.Add(new Claim(TenantClaimsPrincipalFactory.TipoClaimActivacionPendiente, pendiente));

        var contexto = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba")),
        };

        contexto.Request.Path = ruta;
        return contexto;
    }
}
