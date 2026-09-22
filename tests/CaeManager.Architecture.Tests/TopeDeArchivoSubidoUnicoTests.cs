using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El tope de 10 MB por archivo subido vive solo en
/// <c>CaeManager.Application.Common.LimitesArchivoSubido</c>. Hasta el
/// 2026-09-23 estaba copiado en nueve constantes privadas (siete componentes
/// de Web y dos validadores de Application) que coincidían por casualidad:
/// cambiar una dejaba la pantalla y el comando con topes distintos. Este
/// trinquete falla si vuelve a aparecer el literal en otro fichero de
/// <c>src/</c>.
///
/// <para>
/// Qué ve: <c>10 * 1024 * 1024</c> (con o sin sufijo <c>L</c> y con
/// cualquier espaciado) y <c>10485760</c> (con o sin separadores <c>_</c>) en
/// <c>.cs</c> y <c>.razor</c>. No ve otras formas de escribir el mismo número
/// (<c>10 &lt;&lt; 20</c>, <c>0xA00000</c>) ni un tope distinto elegido a
/// propósito para otra cosa (5 MB de firma y sello, 3 y 25 MB de correo), que
/// no son este tope.
/// </para>
/// </summary>
public class TopeDeArchivoSubidoUnicoTests
{
    private const string FicheroDeLaConstante = "src/CaeManager.Application/Common/LimitesArchivoSubido.cs";

    private static readonly Regex Literal = new(
        @"\b10L?\s*\*\s*1024L?\s*\*\s*1024L?\b|\b10_?485_?760L?\b", RegexOptions.Compiled);

    [Fact]
    public void El_tope_de_10_MB_por_archivo_solo_se_escribe_en_LimitesArchivoSubido()
    {
        var raiz = RaizDelRepositorio();
        var conLiteral = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Select(a => Path.GetRelativePath(raiz, a).Replace('\\', '/'))
            .Where(r => !r.Split('/').Any(parte => parte is "bin" or "obj"))
            .Where(r => Literal.IsMatch(File.ReadAllText(Path.Combine(raiz, r))))
            .ToList();

        conLiteral.Should().Contain(FicheroDeLaConstante,
            "control positivo: si el detector no ve la propia constante, no puede ver ninguna copia");
        conLiteral.Should().Equal([FicheroDeLaConstante],
            "el tope por archivo subido se usa como LimitesArchivoSubido.TamanoMaximoBytes, no se copia");
    }

    [Theory]
    [InlineData("private const long Tope = 10 * 1024 * 1024;")]
    [InlineData("const long tope = 10L*1024L*1024L;")]
    [InlineData("OpenReadStream(10485760)")]
    [InlineData("var tope = 10_485_760;")]
    public void El_detector_ve_las_formas_habituales_del_literal(string codigo)
    {
        Literal.IsMatch(codigo).Should().BeTrue();
    }

    [Theory]
    [InlineData("private const long Tope = 5 * 1024 * 1024;")]
    [InlineData("const long tope = 110 * 1024 * 1024;")]
    [InlineData("const long tope = 10 * 1024 * 10240;")]
    public void El_detector_no_confunde_otros_topes(string codigo)
    {
        Literal.IsMatch(codigo).Should().BeFalse();
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
