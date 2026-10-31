using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El cierre por inactividad no puede convertirse en vigilancia de periféricos: la circular
/// de medición de tiempos promete a la plantilla que no se monitorizan teclado, ratón ni
/// desplazamiento. <c>actividad-sesion.js</c> solo escucha acciones sobre la interfaz. Este
/// trinquete vigila además que el guion se cargue en todas las páginas y que ningún inicio de
/// sesión pida una cookie persistente: el comportamiento lo prueba <c>CaducidadSesionTests</c>.
/// </summary>
public class SenalActividadSesionTests
{
    [Fact]
    public void El_guion_solo_escucha_acciones_sobre_la_interfaz()
    {
        var guion = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "wwwroot", "js", "actividad-sesion.js"));
        var codigo = Regex.Replace(guion, @"^\s*//.*$", "", RegexOptions.Multiline);

        Regex.Match(codigo, @"for \(const tipo of \[(?<tipos>[^\]]*)\]").Groups["tipos"].Value
            .Should().Be("'click', 'input', 'change', 'submit'");
        codigo.Should().NotMatchRegex("mouse|pointer|key|wheel|scroll|touch|drag",
            "ni aun con otro mecanismo: un evento de periférico rompe la promesa de la circular");
        Regex.Matches(codigo, "addEventListener").Should().ContainSingle();
    }

    [Fact]
    public void App_carga_el_guion()
    {
        var app = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Components", "App.razor"));
        app.Should().Contain("js/actividad-sesion.js");
    }

    [Fact]
    public void Ningun_inicio_de_sesion_pide_una_cookie_persistente()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var ficheros = Directory.EnumerateFiles(web, "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();

        ficheros.Should().Contain(f => f.EndsWith("Login.razor"), "control: el barrido alcanza los inicios de sesión");
        ficheros.Where(f => Regex.IsMatch(File.ReadAllText(f), @"[iI]sPersistent\s*[:=]\s*true")).Should().BeEmpty();
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
