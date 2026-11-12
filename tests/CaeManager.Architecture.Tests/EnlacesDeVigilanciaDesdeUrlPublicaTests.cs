using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los enlaces con token de <c>VigilanciaInicioSesionService</c> (aviso «No he sido yo» y
/// contraseña nueva tras confirmarlo) salen de <c>Smtp:UrlBasePublica</c> mediante
/// <c>UrlBase()</c> y <c>UrlBasePublica.Resolver</c>, nunca del Host de la petición, que controla
/// quien la envía (OWASP ASVS 4.0.3 V2.5, V5.1.5). El comportamiento lo prueba
/// <c>AccesoCuentaEscenaTests</c> en Web.Tests con un Host forjado para la confirmación; el aviso
/// tras un inicio de sesión inusual no tiene arnés propio, así que este trinquete vigila en el
/// fuente que los dos enlaces pasen por <c>UrlBase()</c> y que la base de la petición solo la lea
/// <c>Resolver</c> (que la usa únicamente en Development sin base configurada).
/// </summary>
public class EnlacesDeVigilanciaDesdeUrlPublicaTests
{
    [Fact]
    public void Los_enlaces_con_token_de_la_vigilancia_usan_la_url_publica_y_no_el_host_de_la_peticion()
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(),
            "src", "CaeManager.Infrastructure", "Seguridad", "VigilanciaInicioSesionService.cs"));

        var enlaces = Regex.Matches(fuente, @"\$""\{(?<base>[^}]+)\}/cuenta/");
        enlaces.Should().HaveCount(2, "el aviso «No he sido yo» y el enlace de contraseña nueva");
        enlaces.Select(e => e.Groups["base"].Value).Should().OnlyContain(b => b == "urlBase");
        Regex.Matches(fuente, @"UrlBase\(\) is \{ \} urlBase").Should().HaveCount(2,
            "cada enlace toma urlBase de UrlBase(), no de otra variable");

        fuente.Should().Contain("UrlBasePublica.Resolver(smtp.Value.UrlBasePublica, BaseDeLaPeticion, entorno.IsDevelopment())",
            "UrlBase() aplica la regla única: la configurada y el Host de la petición solo en Development");
        Regex.Matches(fuente, @"\bBaseDeLaPeticion\b").Should().HaveCount(2,
            "la base de la petición solo se declara y se pasa a Resolver; nadie más la usa");
        Regex.Matches(fuente, @"\.Host\b").Should().HaveCount(2,
            "solo BaseDeLaPeticion lee el Host (HasValue y la composición)");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
