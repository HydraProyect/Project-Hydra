using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// En un <c>.razor.css</c> el combinador que atraviesa el aislamiento de Blazor es <c>::deep</c>, con dos puntos
/// y sin paréntesis. <c>:deep(...)</c> es sintaxis de Vue: Blazor no la reconoce y el navegador descarta la
/// regla entera, sin error ni aviso.
///
/// <para>
/// <b>Por qué hace falta.</b> Defecto visto el 2026-10-18 en el Cuadro de dirección: cinco reglas escritas con
/// <c>:deep(...)</c> no se aplicaban nunca, y con ellas fallaban el título de la pantalla, el de las tarjetas, sus
/// márgenes y el margen del minigráfico, todo a la vez y con las pruebas en verde.
/// </para>
///
/// <para>
/// <b>Contrato efectivo.</b> Lee el texto de cada <c>.razor.css</c> bajo <c>src</c>, sin comentarios: no calcula
/// la cascada ni comprueba que <c>::deep</c> esté bien colocado (va detrás de un selector con espacio).
/// </para>
/// </summary>
public sealed partial class CssAisladoSoloUsaDeepConDobleDosPuntosTests
{
    [Fact]
    public void Ningun_razor_css_usa_deep_con_un_solo_dos_puntos()
    {
        var ficheros = FicherosRazorCss();
        ficheros.Should().NotBeEmpty("control positivo: si no se encuentra ningún .razor.css, el test no mide nada");

        var infractores = ficheros
            .SelectMany(f => DeepErroneo().Matches(SinComentarios(File.ReadAllText(f))).Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();

        infractores.Should().BeEmpty(
            "«:deep(...)» no existe en Blazor y la regla se descarta entera; se escribe «.padre ::deep .hijo» (dos puntos, sin paréntesis)");
    }

    [Fact]
    public void El_detector_distingue_la_sintaxis_de_Blazor_de_la_de_Vue()
    {
        DeepErroneo().IsMatch(".a :deep(.b) { x: y; }").Should().BeTrue();
        DeepErroneo().IsMatch(".a ::deep .b { x: y; }").Should().BeFalse();
        DeepErroneo().IsMatch(".a ::deep(.b) { x: y; }").Should().BeFalse("el aviso es solo del caso de un punto; ::deep( se ve igualmente en revisión");
    }

    private static string SinComentarios(string css) => ComentarioCss().Replace(css, string.Empty);

    private static List<string> FicherosRazorCss()
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src");
        return Directory.Exists(carpeta)
            ? Directory.EnumerateFiles(carpeta, "*.razor.css", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToList()
            : [];
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }

    [GeneratedRegex(@"(?<!:):deep\b")]
    private static partial Regex DeepErroneo();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex ComentarioCss();
}
