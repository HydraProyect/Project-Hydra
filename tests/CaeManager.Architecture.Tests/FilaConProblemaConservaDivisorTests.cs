using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La fila con problema de las fichas 360 (<c>.fila-relacion[data-tono]</c>) conserva su línea
/// divisoria y no redondea las esquinas.
///
/// <para>
/// <b>Por qué hace falta.</b> Defecto visto el 2026-10-09: la regla ponía
/// <c>border-top-color: transparent</c> y <c>border-radius</c> a la fila teñida, así que varias
/// filas con problema seguidas se veían como una sola franja de color, sin distinguir cada
/// elemento.
/// </para>
///
/// <para>
/// <b>Contrato efectivo.</b> Lee el texto de los <c>.css</c> de <c>src/CaeManager.Web</c> y, en
/// toda regla cuyo selector nombre <c>.fila-relacion</c> junto con <c>[data-tono</c>, prohíbe
/// declarar cualquier propiedad de borde (<c>border*</c>, que incluye <c>border-radius</c>).
/// No renderiza nada: no ve un borde tapado por otra vía (un <c>box-shadow</c>, un
/// <c>outline</c>, un estilo en línea) ni una regla que llegue a la fila sin nombrar
/// <c>data-tono</c>. Que la línea se vea de verdad se comprueba en el navegador.
/// </para>
///
/// <para>
/// <b>Límite declarado.</b> Casa por texto del selector: no ve CSS anidado
/// (<c>.fila-relacion { &amp;[data-tono] { … } }</c>) y daría rojo con una regla legítima que
/// nombre el atributo para excluirlo (<c>.fila-relacion:not([data-tono])</c>). Hoy no existe
/// ninguna de las dos formas.
/// </para>
/// </summary>
public sealed partial class FilaConProblemaConservaDivisorTests
{
    [Fact]
    public void La_fila_con_problema_no_declara_borde_ni_radio()
    {
        var reglas = ReglasDeFilaConProblema();

        // Control positivo: si el detector no encuentra la regla del tinte, el vacío de abajo no dice nada.
        reglas.Should().Contain(r => r.Declaraciones.Contains("background-image", StringComparison.Ordinal),
            "la regla del tinte de la fila con problema tiene que estar entre las leídas");

        var infractoras = reglas
            .Where(r => DeclaracionDeBorde().IsMatch(r.Declaraciones))
            .Select(r => $"{r.Fichero}: {r.Selector}")
            .ToList();

        infractoras.Should().BeEmpty(
            "la fila teñida conserva la línea divisoria de su fila y no redondea las esquinas");
    }

    [Theory]
    [InlineData("border-top-color: transparent;")]
    [InlineData("border-radius: var(--radius-md);")]
    [InlineData("background: none;\n    border: 0;")]
    public void El_detector_ve_una_declaracion_de_borde(string declaraciones) =>
        DeclaracionDeBorde().IsMatch(declaraciones).Should().BeTrue();

    private static List<(string Fichero, string Selector, string Declaraciones)> ReglasDeFilaConProblema()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var reglas = new List<(string, string, string)>();

        foreach (var fichero in Directory.EnumerateFiles(web, "*.css", SearchOption.AllDirectories))
        {
            var relativo = Path.GetRelativePath(web, fichero).Replace('\\', '/');
            if (relativo.StartsWith("bin/", StringComparison.Ordinal) || relativo.StartsWith("obj/", StringComparison.Ordinal))
                continue;

            var css = Comentario().Replace(File.ReadAllText(fichero), string.Empty);
            foreach (Match regla in Regla().Matches(css))
            {
                var selector = regla.Groups[1].Value.Trim();
                if (selector.Contains(".fila-relacion", StringComparison.Ordinal) && selector.Contains("[data-tono", StringComparison.Ordinal))
                    reglas.Add((relativo, selector, regla.Groups[2].Value));
            }
        }

        return reglas;
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comentario();

    [GeneratedRegex(@"([^{}]+)\{([^{}]*)\}")]
    private static partial Regex Regla();

    [GeneratedRegex(@"(?<![\w-])border[\w-]*\s*:")]
    private static partial Regex DeclaracionDeBorde();
}
