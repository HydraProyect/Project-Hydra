using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S-A-6: el cupo de la API pública no puede salir de <c>HttpContext.User</c>. El limitador corre
/// antes de <c>UseAuthorization</c> y el esquema por defecto es la cookie, de modo que el principal
/// de una clave API nunca existe a esa altura y todo el tráfico con clave compartía un único cupo.
/// El comportamiento del reparto lo prueba <c>LimitadorApiPublicaTests</c> en Web.Tests; este
/// trinquete vigila que <c>Program.cs</c> use esas funciones (el proyecto no tiene un
/// <c>WebApplicationFactory</c> que lo observe) y que la política no vuelva a leer el principal.
/// </summary>
public class LimitadorApiPublicaRegistradoEnProgramTests
{
    [Fact]
    public void Program_reparte_el_cupo_de_la_API_con_LimitadorApiPublica()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().Contain("LimitadorApiPublica.ParticionPorLlamante(",
            "la política ApiPublica debe repartir el cupo por clave presentada o por IP");
        program.Should().Contain("LimitadorApiPublica.ParticionGlobal(",
            "sin el techo por IP, rotar claves inventadas daría un cupo nuevo en cada petición");
    }

    [Fact]
    public void La_politica_ApiPublica_no_particiona_por_el_principal_autenticado()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        program.Should().NotContain("\"sin-tenant\"",
            "particionar por HttpContext.User agrupa todo el tráfico con clave en un único cupo");
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
