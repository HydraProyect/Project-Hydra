using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// S-A-6: el cupo de la API pública no puede salir de <c>HttpContext.User</c>. El limitador corre
/// antes de <c>UseAuthorization</c> y el esquema por defecto es la cookie, de modo que el principal
/// de una clave API nunca existe a esa altura y todo el tráfico con clave compartía un único cupo.
/// El comportamiento del reparto lo prueba <c>LimitadorApiPublicaTests</c> en Web.Tests; este
/// trinquete vigila el cableado de <c>Program.cs</c> (el proyecto no tiene un
/// <c>WebApplicationFactory</c> que lo observe), sobre el código sin comentarios.
/// </summary>
public class LimitadorApiPublicaRegistradoEnProgramTests
{
    [Fact]
    public void La_politica_ApiPublica_reparte_el_cupo_con_LimitadorApiPublica_y_no_con_el_principal()
    {
        var codigo = ProgramSinComentarios();

        var politica = Regex.Match(codigo,
            @"AddPolicy\(LimitadorApiPublica\.Politica,.*?\);", RegexOptions.Singleline);

        politica.Success.Should().BeTrue("la política ApiPublica debe registrarse con la constante de LimitadorApiPublica");
        politica.Value.Should().Contain("LimitadorApiPublica.ParticionPorLlamante(")
            .And.NotContain(".User", "particionar por HttpContext.User agrupa todo el tráfico con clave en un único cupo")
            .And.NotContain("sin-tenant");
    }

    [Fact]
    public void El_limitador_global_devuelve_el_techo_por_IP_de_la_API()
    {
        var codigo = ProgramSinComentarios();

        Regex.IsMatch(codigo,
                @"LimitadorApiPublica\.ParticionGlobal\([^)]*\)\s+is\s*\{\s*\}\s+(\w+)\)\s*return\s+\1\s*;")
            .Should().BeTrue("sin devolver el techo por IP, rotar claves inventadas daría un cupo nuevo en cada petición");
    }

    [Fact]
    public void El_grupo_de_la_API_v1_aplica_la_politica_de_limitacion()
    {
        var codigo = ProgramSinComentarios();

        Regex.IsMatch(codigo, @"MapGroup\(""/api/v1""\)[^;]*\.RequireRateLimiting\((""ApiPublica""|LimitadorApiPublica\.Politica)\)")
            .Should().BeTrue("sin RequireRateLimiting el grupo /api/v1 solo tendría el techo por IP");
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
