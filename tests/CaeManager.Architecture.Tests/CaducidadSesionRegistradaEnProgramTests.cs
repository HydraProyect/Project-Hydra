using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>Program.cs</c> aplica a la cookie de aplicación la caducidad de TALVEG (8 h
/// de inactividad, tope de 12 h desde el inicio de sesión, Secure) y registra el
/// HSTS de 365 días. Sin la llamada vuelve la cookie de Identity por defecto: 14
/// días deslizante sin tope. El comportamiento lo prueba <c>CaducidadSesionTests</c>
/// en Web.Tests; este trinquete solo vigila que <c>Program.cs</c> lo llame, y
/// después de <c>OmitirRevalidacionDeStampEnRuta</c>, para que el tope envuelva
/// también a esa ruta.
/// </summary>
public class CaducidadSesionRegistradaEnProgramTests
{
    [Fact]
    public void Program_configura_la_caducidad_de_la_cookie_y_el_HSTS()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().Contain("CaducidadSesion.ConfigurarHsts(builder.Services);",
            "sin esa llamada UseHsts emite el max-age por defecto de 30 días");
        var omitir = program.IndexOf("OmitirRevalidacionDeStampEnRuta.Configurar(options", StringComparison.Ordinal);
        var caducidad = program.IndexOf("CaducidadSesion.Configurar(options, builder.Environment);", StringComparison.Ordinal);
        omitir.Should().BePositive("control: la referencia de orden sigue en Program.cs");
        caducidad.Should().BePositive("sin esa llamada la cookie vuelve a durar 14 días deslizante, sin tope de 12 h");
        caducidad.Should().BeGreaterThan(omitir,
            "el tope de 12 h ha de envolver a OmitirRevalidacionDeStampEnRuta, no al revés");
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
