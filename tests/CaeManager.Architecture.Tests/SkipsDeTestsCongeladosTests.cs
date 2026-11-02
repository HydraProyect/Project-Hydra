using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// MJ-08 (plan de mejora del código, 2026-10-31): los tests omitidos (<c>Skip =</c>, <c>Skip.If</c>,
/// <c>Skip.IfNot</c>) de <c>tests/</c>, congelados por fichero.
///
/// <para>
/// <b>Por qué.</b> Un <c>Skip</c> permanente es una propiedad que dejó de vigilarse sin que nadie lo vea: el verde de CI
/// no distingue «pasó» de «no corrió». Hoy hay omisiones condicionadas a un entorno (clave de IA, Windows/Linux, root) y
/// una permanente sin dueño (<c>RetiradaTenantDemoServiceTests</c>, MJ-09). La lista (<c>Congelados/Skips-de-tests.txt</c>,
/// <see cref="ListaCongelada"/>) fija cuántas hay por fichero: una nueva es <c>NUEVA</c> o <c>CRECE</c> y hay que
/// justificarla en la lista con un comentario <c>#</c>; una retirada baja la lista.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados): (1) <c>[Fact(Skip = …)]</c> escrito en una cadena (los datos de los
/// propios tests del escáner) cuenta como omisión: por eso se excluyen dos ficheros de esta carpeta que traen ejemplos
/// sintéticos (este y <c>ColeccionesDeE2ECongeladasTests</c>), y un <c>Skip</c> real en ellos quedaría fuera;
/// (2) <c>[Trait("Category", …)]</c> o un filtro de CI que excluya tests sin <c>Skip</c> no cuenta; (3) no mide si la
/// condición de un <c>Skip.If</c> se cumple en CI: una omisión «por entorno» que en CI siempre se cumple es en la
/// práctica permanente.
/// </para>
/// </summary>
public class SkipsDeTestsCongeladosTests
{
    private const string Lista = "Skips-de-tests";

    private const string Guia =
        "Un test omitido es una propiedad que deja de vigilarse y el verde no lo enseña. NUEVA o CRECE: no añadas ni subas " +
        "la línea sin motivo; arregla el test o, si la omisión es de entorno, añade la línea en tests/CaeManager.Architecture.Tests/" +
        "Congelados/" + Lista + ".txt con un comentario '#' que diga qué condición la dispara y quién es su dueño. BAJA u " +
        "OBSOLETA: has retirado omisiones; baja la cifra o borra la línea (HYDRA_TRINQUETES_VOLCAR=<directorio> vuelca la medida).";

    private static readonly string[] Excluidos =
    [
        "tests/CaeManager.Architecture.Tests/SkipsDeTestsCongeladosTests.cs",
        "tests/CaeManager.Architecture.Tests/ColeccionesDeE2ECongeladasTests.cs",
    ];

    private static readonly Regex PatronSkipNombrado = new(@"\bSkip\s*=", RegexOptions.Compiled);
    private static readonly Regex PatronSkipDinamico = new(@"\bSkip\s*\.\s*(?:If|IfNot)\s*\(", RegexOptions.Compiled);

    [Fact]
    public void Los_tests_omitidos_solo_decrecen()
    {
        var fallo = ListaCongelada.Verificar(Lista, Medir(FuentesDeTests()), Guia);

        fallo.Should().BeNull();
    }

    [Fact]
    public void El_escaner_ve_la_omision_conocida_de_la_retirada_de_demo()
    {
        // Control positivo independiente de la lista: si el recorrido no leyera tests/, «ningún Skip nuevo» valdría por vacío.
        Medir(FuentesDeTests()).Keys.Should().Contain(u =>
            u.Lugar == "tests/CaeManager.IntegrationTests/Retirada/RetiradaTenantDemoServiceTests.cs");
    }

    [Fact]
    public void El_detector_cuenta_las_formas_que_vigila_e_ignora_comentarios()
    {
        var medido = Medir(new Dictionary<string, string>
        {
            ["a.cs"] = "[Fact(Skip = \"x\")] public void M() { }\n// [Fact(Skip = \"comentado\")]\nSkip.If(true, \"y\"); Skip.IfNot(ok, \"z\");",
            ["b.cs"] = "[Fact] public void M() { }",
        });

        medido.Should().BeEquivalentTo(new Dictionary<Ubicacion, int>
        {
            [new Ubicacion("a.cs", "Skip (atributo)")] = 1,
            [new Ubicacion("a.cs", "Skip.If")] = 2,
        });
    }

    internal static IReadOnlyDictionary<Ubicacion, int> Medir(IReadOnlyDictionary<string, string> fuentes)
    {
        var medido = new Dictionary<Ubicacion, int>();
        foreach (var (ruta, texto) in fuentes.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var limpio = LimpiadorDeComentarios.Quitar(texto, razor: false);
            var nombrado = PatronSkipNombrado.Matches(limpio).Count;
            var dinamico = PatronSkipDinamico.Matches(limpio).Count;
            if (nombrado > 0) medido[new Ubicacion(ruta, "Skip (atributo)")] = nombrado;
            if (dinamico > 0) medido[new Ubicacion(ruta, "Skip.If")] = dinamico;
        }

        return medido;
    }

    private static Dictionary<string, string> FuentesDeTests()
    {
        var raiz = FuentesDeSrc.RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(Path.Combine(raiz, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => (Ruta: Path.GetRelativePath(raiz, a).Replace(separador, '/'), Absoluta: a))
            .Where(f => !Excluidos.Contains(f.Ruta))
            .ToDictionary(f => f.Ruta, f => File.ReadAllText(f.Absoluta), StringComparer.Ordinal);
    }
}
