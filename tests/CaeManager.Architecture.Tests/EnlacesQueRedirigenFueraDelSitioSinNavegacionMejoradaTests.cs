using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Un enlace a un endpoint que responde 302 a Microsoft (conectar un buzón de Microsoft 365, inicio de sesión con
/// Microsoft) no puede pasar por la navegación mejorada de Blazor: haría <c>fetch</c>, la CSP (<c>connect-src 'self'</c>)
/// bloquearía el seguimiento y la barra cambiaría sin que cambiase la página (visto en staging, 2026-11-17).
/// Debe llevar <c>data-enhance-nav="false"</c>. El trinquete mira los <c>.razor</c> de Web: enlaces <c>&lt;a&gt;</c> o
/// <c>&lt;Boton Href=&gt;</c> cuyo destino nombra <c>conectar-*</c>, <c>iniciar-sesion-microsoft</c> o las propiedades que lo construyen.
/// </summary>
public class EnlacesQueRedirigenFueraDelSitioSinNavegacionMejoradaTests
{
    private static readonly Regex EnlaceExterno = new(
        @"<(?:a|Boton)\b[^>]*\b(?:href|Href)=""[^""]*(?:conectar-|iniciar-sesion-microsoft|UrlConectar|EnlaceLoginMicrosoft|EnlaceReautenticacionMicrosoft)[^""]*""[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void Todo_enlace_a_un_endpoint_que_redirige_a_Microsoft_lleva_data_enhance_nav_false()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var enlaces = Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => EnlaceExterno.Matches(File.ReadAllText(f)).Select(m => (Fichero: Path.GetRelativePath(web, f), Etiqueta: m.Value)))
            .ToList();

        enlaces.Should().NotBeEmpty("el control positivo prueba que el patrón encuentra los enlaces conocidos");
        enlaces.Where(e => !e.Etiqueta.Contains("data-enhance-nav=\"false\"", StringComparison.Ordinal))
            .Select(e => $"{e.Fichero}: {e.Etiqueta}")
            .Should().BeEmpty("la navegación mejorada intercepta el enlace y la CSP bloquea la redirección a Microsoft");
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
