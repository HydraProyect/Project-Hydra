using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>Program.cs</c> registra la configuración de antiforgery de TALVEG. Sin la
/// llamada rige <c>CookieSecurePolicy.None</c> y la cookie sale por HTTPS sin
/// <c>Secure</c> (medido en producción el 2026-09-19). El comportamiento de
/// <c>AntiforgeryDeTalveg.Configurar</c> lo prueba <c>AntiforgeryDeTalvegTests</c>
/// en Web.Tests; este trinquete solo vigila que <c>Program.cs</c> lo llame,
/// porque el proyecto no tiene un <c>WebApplicationFactory</c> que lo observe.
/// </summary>
public class AntiforgeryRegistradoEnProgramTests
{
    [Fact]
    public void Program_registra_AddAntiforgery_con_la_configuracion_de_TALVEG()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().Contain("AddAntiforgery(AntiforgeryDeTalveg.Configurar)",
            "sin esa llamada la cookie de antiforgery vuelve a salir por HTTPS sin la marca Secure");
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
