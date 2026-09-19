using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La cookie de antiforgery lleva <c>Secure</c> cuando la petición es HTTPS y
/// no la lleva sobre HTTP (donde el navegador la descartaría). Se observa con
/// el <c>IAntiforgery</c> real del framework, no con una copia de la lógica:
/// el <c>Set-Cookie</c> que sale es el que sale en producción.
///
/// <para>
/// Límite del instrumento: el proyecto no expone un <c>WebApplicationFactory</c>,
/// así que este test no ve que <c>Program.cs</c> llame a
/// <see cref="AntiforgeryDeTalveg.Configurar"/>; eso lo vigila
/// <c>AntiforgeryRegistradoEnProgramTests</c> en Architecture.Tests. Que
/// <c>IsHttps</c> refleje el esquema original tras el proxy lo garantiza
/// <c>UseForwardedHeaders</c> (medido por <c>CabecerasDeProxyDeBordeTests</c>),
/// no este test.
/// </para>
/// </summary>
public class AntiforgeryDeTalvegTests
{
    [Fact]
    public void Sobre_HTTPS_la_cookie_de_antiforgery_lleva_Secure()
        => SetCookieDeAntiforgery(esquema: "https", configurar: AntiforgeryDeTalveg.Configurar)
            .Should().ContainEquivalentOf("; secure");

    [Fact]
    public void Sobre_HTTP_la_cookie_de_antiforgery_no_lleva_Secure()
        => SetCookieDeAntiforgery(esquema: "http", configurar: AntiforgeryDeTalveg.Configurar)
            .Should().NotContainEquivalentOf("secure");

    /// <summary>
    /// Control positivo del instrumento: con el valor por defecto del
    /// framework (lo que había antes del cambio) el mismo arnés SÍ ve una
    /// cookie sin <c>Secure</c> sobre HTTPS. Sin este caso, un verde arriba
    /// podría ser un test que no distingue esquema.
    /// </summary>
    [Fact]
    public void Con_el_valor_por_defecto_del_framework_HTTPS_no_lleva_Secure()
        => SetCookieDeAntiforgery(esquema: "https", configurar: _ => { })
            .Should().NotContainEquivalentOf("secure");

    private static string SetCookieDeAntiforgery(string esquema, Action<AntiforgeryOptions> configurar)
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAntiforgery(configurar);
        using var proveedor = servicios.BuildServiceProvider();

        var contexto = new DefaultHttpContext { RequestServices = proveedor };
        contexto.Request.Scheme = esquema;
        contexto.Request.Host = new HostString("localhost");

        proveedor.GetRequiredService<IAntiforgery>().GetAndStoreTokens(contexto);

        var cabeceras = contexto.Response.Headers.SetCookie.ToArray();
        var cookie = cabeceras.SingleOrDefault(c => c is not null && c.Contains(".AspNetCore.Antiforgery", StringComparison.Ordinal));
        cookie.Should().NotBeNull("el instrumento tiene que ver la cookie de antiforgery para poder afirmar nada sobre ella");
        return cookie!;
    }
}
