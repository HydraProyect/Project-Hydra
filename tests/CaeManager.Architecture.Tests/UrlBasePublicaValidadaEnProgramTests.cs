using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>Program.cs</c> valida <c>Smtp:UrlBasePublica</c> al arrancar: fuera de Development es
/// obligatoria (https) y sin ella la aplicación no arranca. El comportamiento del validador y
/// del arranque lo prueban <c>EnlacesPublicosTests</c> en Web.Tests; este trinquete solo vigila
/// que <c>Program.cs</c> registre ambas piezas, porque el proyecto no tiene un
/// <c>WebApplicationFactory</c> que lo observe.
/// </summary>
public class UrlBasePublicaValidadaEnProgramTests
{
    [Fact]
    public void Program_registra_el_validador_de_arranque_de_la_url_base_publica()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().Contain("ValidadorUrlBasePublica",
            "sin el validador, una instancia sin Smtp:UrlBasePublica arranca y los enlaces de cuenta fallan en silencio");
        program.Should().Contain("AddOptions<CaeManager.Infrastructure.Email.SmtpEmailOptions>().ValidateOnStart()",
            "registrar el validador no basta: sin ValidateOnStart solo se evalúa al leer las opciones, no al arrancar");
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
