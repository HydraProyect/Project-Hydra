using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S-B-6 y S-B-7: el comportamiento de la guarda de identidad y de la validación de intervalos lo
/// prueba <c>SeguridadConfiguracionArranqueTests</c> en Web.Tests sobre un host genérico. Este
/// trinquete vigila el cableado de <c>Program.cs</c> (el proyecto no tiene un
/// <c>WebApplicationFactory</c> que lo observe), sobre el código sin comentarios. Sin él, quitar
/// <c>AddIntervalosRevalidacion</c> dejaría resolverse las opciones con sus valores por defecto e
/// ignorar la configuración en silencio, y nada fallaría.
/// </summary>
public class ArranqueEndurecidoRegistradoEnProgramTests
{
    [Fact]
    public void Program_rechaza_la_identidad_administrativa_en_produccion_antes_de_registrar_servicios()
    {
        var codigo = ProgramSinComentarios();

        var guarda = codigo.IndexOf(
            "RechazarIdentidadAdministrativaEnProduccion(builder.Configuration, builder.Environment)", StringComparison.Ordinal);
        var primerRegistro = codigo.IndexOf("builder.Services.", StringComparison.Ordinal);

        guarda.Should().BeGreaterThan(0, "Program.cs debe llamar a la guarda de S-B-6 con la configuración del builder");
        guarda.Should().BeLessThan(primerRegistro, "la guarda va antes de registrar ningún servicio");
    }

    [Fact]
    public void Program_registra_los_intervalos_validados_y_el_sello_de_seguridad_sale_de_ellos()
    {
        var codigo = ProgramSinComentarios();

        codigo.Should().Contain("builder.Services.AddIntervalosRevalidacion(builder.Configuration);");
        Regex.IsMatch(codigo,
                @"AddOptions<SecurityStampValidatorOptions>\(\)\s*\.Configure<[\w.]*IOptions<IntervalosRevalidacionOptions>>\(\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)\s*=>\s*\1\.ValidationInterval\s*=\s*TimeSpan\.FromSeconds\(\s*\2\.Value\.SesionSegundos\s*\)\s*\)")
            .Should().BeTrue("el intervalo del sello de seguridad tiene que salir de las opciones validadas, no de IConfiguration");
        Regex.IsMatch(codigo, @"GetValue\w*\(\s*""(Sesion:IntervaloRevalidacionSegundos|Circuit:RevalidacionIntervaloSegundos)""")
            .Should().BeFalse("leer los intervalos directamente de IConfiguration esquiva el techo validado");
    }

    private static string ProgramSinComentarios()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));
        return string.Join('\n', program.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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
