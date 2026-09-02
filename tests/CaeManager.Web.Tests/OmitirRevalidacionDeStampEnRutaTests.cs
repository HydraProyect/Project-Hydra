using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-3 (Sentry DOTNET-8): sin este envoltorio, la revalidación del security
/// stamp de <c>SecurityStampValidator</c> golpea la base en CUALQUIER
/// petición autenticada — incluida <c>/Error</c>, la propia pantalla de
/// fallo. Estas pruebas cubren las dos direcciones: la ruta indicada salta
/// la revalidación, y cualquier otra ruta la conserva intacta.
/// </summary>
public class OmitirRevalidacionDeStampEnRutaTests
{
    private static CookieValidatePrincipalContext ContextoPara(string ruta)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = ruta;
        var scheme = new AuthenticationScheme("Identity.Application", null, typeof(CookieAuthenticationHandler));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity()), "Identity.Application");
        return new CookieValidatePrincipalContext(httpContext, scheme, new CookieAuthenticationOptions(), ticket);
    }

    [Fact]
    public async Task La_ruta_indicada_salta_la_revalidacion_del_stamp()
    {
        var llamadasAlOriginal = 0;
        var opciones = new CookieAuthenticationOptions
        {
            Events = new CookieAuthenticationEvents { OnValidatePrincipal = _ => { llamadasAlOriginal++; return Task.CompletedTask; } },
        };
        OmitirRevalidacionDeStampEnRuta.Configurar(opciones, "/Error");

        await opciones.Events.OnValidatePrincipal(ContextoPara("/Error"));

        llamadasAlOriginal.Should().Be(0, "la ruta /Error no debe tocar la base a través de SecurityStampValidator");
    }

    [Fact]
    public async Task Cualquier_otra_ruta_conserva_la_revalidacion_normal()
    {
        // Control positivo: la ceguera tiene que quedar acotada a la ruta
        // indicada. El resto del sitio sigue revalidando el stamp como hoy.
        var llamadasAlOriginal = 0;
        var opciones = new CookieAuthenticationOptions
        {
            Events = new CookieAuthenticationEvents { OnValidatePrincipal = _ => { llamadasAlOriginal++; return Task.CompletedTask; } },
        };
        OmitirRevalidacionDeStampEnRuta.Configurar(opciones, "/Error");

        await opciones.Events.OnValidatePrincipal(ContextoPara("/documentos"));
        await opciones.Events.OnValidatePrincipal(ContextoPara("/"));

        llamadasAlOriginal.Should().Be(2, "cualquier otra ruta sigue revalidando el stamp exactamente igual que antes");
    }

    [Fact]
    public async Task La_comparacion_de_ruta_ignora_mayusculas()
    {
        var llamadasAlOriginal = 0;
        var opciones = new CookieAuthenticationOptions
        {
            Events = new CookieAuthenticationEvents { OnValidatePrincipal = _ => { llamadasAlOriginal++; return Task.CompletedTask; } },
        };
        OmitirRevalidacionDeStampEnRuta.Configurar(opciones, "/Error");

        await opciones.Events.OnValidatePrincipal(ContextoPara("/error"));

        llamadasAlOriginal.Should().Be(0, "la ruta real declarada en Error.razor es \"/Error\", pero ASP.NET Core resuelve rutas sin distinguir mayúsculas");
    }

    [Fact]
    public async Task La_comparacion_es_exacta_no_por_prefijo()
    {
        // Acotado a propósito (revisión de la coordinadora): un sub-path que
        // empiece igual (p. ej. "/Error/algo", si algún día existiera) no
        // debe colarse por el salto — Error.razor solo declara "/Error", sin
        // sub-rutas, y el salto no debe ser más ancho que eso.
        var llamadasAlOriginal = 0;
        var opciones = new CookieAuthenticationOptions
        {
            Events = new CookieAuthenticationEvents { OnValidatePrincipal = _ => { llamadasAlOriginal++; return Task.CompletedTask; } },
        };
        OmitirRevalidacionDeStampEnRuta.Configurar(opciones, "/Error");

        await opciones.Events.OnValidatePrincipal(ContextoPara("/Error/algo"));
        await opciones.Events.OnValidatePrincipal(ContextoPara("/ErrorExtra"));

        llamadasAlOriginal.Should().Be(2, "el salto es una coincidencia exacta de ruta, no un prefijo — ninguna otra ruta que empiece igual debe quedar exenta");
    }
}
