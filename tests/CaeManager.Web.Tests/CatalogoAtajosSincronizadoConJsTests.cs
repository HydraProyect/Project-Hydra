using System.Text.RegularExpressions;
using CaeManager.Web.Features.AtajosGlobales;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// HO-006-01 (REC-006): <see cref="CatalogoAtajos.DestinosNavegacion"/> es
/// la fuente de verdad en C#, pero el teclado real pasa primero por
/// <c>wwwroot/js/atajos-globales.js</c> — su <c>TECLAS_DESTINO</c> decide,
/// del lado del navegador, qué letra llega a invocar <c>IrA</c>. Los dos
/// arrays no pueden compartir tipo (uno es C#, el otro JS): si alguien añade
/// una letra a <see cref="CatalogoAtajos.DestinosNavegacion"/> —o a la
/// chuleta— y olvida <c>TECLAS_DESTINO</c>, el atajo queda "anunciado" (en
/// el diccionario y en la ayuda) pero desconectado del teclado, y un test
/// que llame a <c>IrA()</c> directamente (como <c>AtajosGlobalesTests</c>)
/// no lo detecta: bypasa el JS por completo. Este test lee el fichero JS
/// como texto — mismo patrón que los ratchets de
/// <c>CaeManager.Architecture.Tests</c> — y falla nombrando la letra que
/// sobra o falta en cualquiera de los dos lados.
/// </summary>
public class CatalogoAtajosSincronizadoConJsTests
{
    [Fact]
    public void TeclasDestino_del_js_coincide_exactamente_con_CatalogoAtajos()
    {
        var contenidoJs = LeerAtajosGlobalesJs();
        var match = Regex.Match(contenidoJs, @"TECLAS_DESTINO\s*=\s*\[(?<teclas>[^\]]*)\]");

        match.Success.Should().BeTrue("atajos-globales.js debe declarar TECLAS_DESTINO como un array literal — si cambió de forma, actualiza este test");

        var teclasJs = Regex.Matches(match.Groups["teclas"].Value, @"'(\w)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var teclasCSharp = CatalogoAtajos.DestinosNavegacion.Keys.ToHashSet();

        teclasJs.Should().BeEquivalentTo(teclasCSharp,
            "cada letra de CatalogoAtajos.DestinosNavegacion debe poder dispararse desde el teclado (y viceversa) — " +
            "una letra en un lado y no en el otro es un atajo que no funciona o que nunca se anuncia");
    }

    /// <summary>
    /// Mismo riesgo para la sección «Dentro de una lista» de la chuleta: <see cref="CatalogoAtajos.Lista"/>
    /// anuncia teclas que solo funcionan si <c>atajos-lista.js</c> las admite en su
    /// <c>TECLAS_ADMITIDAS</c> (la «f» de «Filtrar esta pantalla» entró así, por los dos lados).
    /// Las teclas del catálogo se separan por « / » («j / k»).
    /// </summary>
    [Fact]
    public void Teclas_admitidas_del_js_de_lista_coinciden_con_CatalogoAtajos_Lista()
    {
        var contenidoJs = LeerJs("atajos-lista.js");
        var match = Regex.Match(contenidoJs, @"TECLAS_ADMITIDAS\s*=\s*\[(?<teclas>[^\]]*)\]");

        match.Success.Should().BeTrue("atajos-lista.js debe declarar TECLAS_ADMITIDAS como un array literal — si cambió de forma, actualiza este test");

        var teclasJs = Regex.Matches(match.Groups["teclas"].Value, @"'(\w+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var teclasCatalogo = CatalogoAtajos.Lista
            .SelectMany(a => a.Tecla.Split(" / ", StringSplitOptions.TrimEntries))
            .ToHashSet();

        teclasJs.Should().BeEquivalentTo(teclasCatalogo,
            "la chuleta solo puede anunciar teclas de lista que atajos-lista.js reparte, y al revés");
    }

    /// <summary>
    /// KeyTips: <c>keytips.js</c> veta sus <c>LETRAS_ESTABLES</c> a los controles que deducen la
    /// letra. Si el catálogo gana una letra y el JS no, una pastilla de filtro puede quedarse con
    /// ella en una pantalla y el control compartido con otra distinta en la siguiente.
    /// </summary>
    [Fact]
    public void Letras_estables_del_js_de_KeyTips_coinciden_con_CatalogoAtajos_KeyTips()
    {
        var contenidoJs = LeerJs("keytips.js");
        var match = Regex.Match(contenidoJs, @"LETRAS_ESTABLES\s*=\s*\[(?<letras>[^\]]*)\]");

        match.Success.Should().BeTrue("keytips.js debe declarar LETRAS_ESTABLES como un array literal — si cambió de forma, actualiza este test");

        var letrasJs = Regex.Matches(match.Groups["letras"].Value, @"'(\w)'")
            .Select(m => m.Groups[1].Value)
            .ToList();

        letrasJs.Should().BeEquivalentTo(CatalogoAtajos.KeyTips.Select(a => a.Tecla),
            "las letras estables de KeyTips se declaran en los dos lados o en ninguno");
    }

    [Fact]
    public void Ninguna_letra_estable_de_KeyTips_se_repite()
    {
        CatalogoAtajos.KeyTips.Select(a => a.Tecla).Should().OnlyHaveUniqueItems(
            "una letra es un control: dos controles con la misma letra en la misma pantalla dejan a uno sin ella");
        CatalogoAtajos.KeyTips.Should().OnlyContain(a => a.Tecla.Length == 1 && char.IsAsciiLetterUpper(a.Tecla[0]),
            "keytips.js compara la tecla en mayúscula con una sola letra");
    }

    /// <summary>
    /// Declaraciones de KeyTips en el marcado: <c>data-keytip="X"</c>, <c>data-keytip-contenedor="X"</c>
    /// y el parámetro <c>Keytip="X"</c> de MenuAcciones. Tres propiedades, leyendo los <c>.razor</c>
    /// como texto:
    /// <list type="bullet">
    /// <item><description>toda letra declarada sale de <see cref="CatalogoAtajos.KeyTips"/> (una letra inventada en una pantalla no la veta el JS ni la conoce la chuleta);</description></item>
    /// <item><description>ningún fichero declara dos veces la misma letra;</description></item>
    /// <item><description>las pantallas que componen dos piezas compartidas no las repiten entre sí: se comprueba sobre la unión de los componentes que comparte un listado (cabecera, barra de filtros, herramientas) más la propia página.</description></item>
    /// </list>
    /// Y la chuleta dice la verdad: <see cref="CatalogoAtajos.KeyTipsConControl"/> es exactamente
    /// el conjunto de letras que alguien declara.
    /// </summary>
    [Fact]
    public void Las_letras_declaradas_en_el_marcado_salen_del_catalogo_y_no_se_repiten_por_pantalla()
    {
        var raizWeb = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        var declaradasPorFichero = Directory.EnumerateFiles(raizWeb, "*.razor", SearchOption.AllDirectories)
            .Where(ruta => !ruta.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !ruta.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(ruta => (Fichero: Path.GetFileName(ruta), Letras: LetrasDeclaradas(File.ReadAllText(ruta))))
            .Where(f => f.Letras.Count > 0)
            .ToList();

        // Control positivo del lector: si el patrón dejara de casar, todo lo de abajo pasaría en vacío.
        declaradasPorFichero.Select(f => f.Fichero).Should().Contain(new[] { "CabeceraListado.razor", "BarraFiltros.razor" },
            "son las dos piezas que declaran las letras de los diez listados");

        var estables = CatalogoAtajos.KeyTips.Select(a => a.Tecla).ToHashSet();
        foreach (var (fichero, letras) in declaradasPorFichero)
        {
            letras.Should().OnlyContain(l => estables.Contains(l), $"{fichero} declara una letra de KeyTips que no está en CatalogoAtajos.KeyTips");
        }

        // BarraFiltros declara F en sus dos modos (pastillas y clásico), que son ramas excluyentes
        // de un mismo @if: es el único fichero donde una letra aparece dos veces.
        var conRepetidas = declaradasPorFichero
            .Where(f => f.Letras.Count != f.Letras.Distinct().Count())
            .Select(f => f.Fichero);
        conRepetidas.Should().BeEquivalentTo(new[] { "BarraFiltros.razor" }, "una letra repetida en un fichero es una letra repetida en pantalla");

        var compartidos = new[] { "CabeceraListado.razor", "BarraFiltros.razor", "BarraHerramientasLista.razor" };
        var letrasCompartidas = declaradasPorFichero.Where(f => compartidos.Contains(f.Fichero))
            .SelectMany(f => f.Letras.Distinct())
            .ToList();
        // S la declaran la cabecera (icono ☑) y BarraHerramientasLista (su botón «Selección múltiple»),
        // que es el mismo conmutador en sus dos generaciones: BarraHerramientasLista lo omite cuando
        // la página lo lleva en la cabecera.
        letrasCompartidas.Where(l => l != "S").Should().OnlyHaveUniqueItems("dos piezas compartidas de un listado no pueden pedir la misma letra");

        var paginas = declaradasPorFichero.Where(f => !compartidos.Contains(f.Fichero) && f.Fichero != "MenuAcciones.razor" && f.Fichero != "PastillaFiltro.razor");
        foreach (var (fichero, letras) in paginas)
        {
            // Una página solo puede añadir letras que las piezas compartidas no usan.
            letras.Should().NotIntersectWith(letrasCompartidas, $"{fichero} repite una letra que ya declara una pieza compartida del listado");
        }

        declaradasPorFichero.SelectMany(f => f.Letras).Distinct().Should().BeEquivalentTo(CatalogoAtajos.KeyTipsConControl,
            "la chuleta anuncia las letras de KeyTipsConControl: ni una que nadie declara, ni una declarada que no anuncia");
    }

    private static List<string> LetrasDeclaradas(string contenido) =>
        Regex.Matches(contenido, @"(?<![\w-])(?:data-keytip|data-keytip-contenedor|Keytip)=""(?<letra>[^""@]+)""")
            .Select(m => m.Groups["letra"].Value)
            .ToList();

    private static string LeerAtajosGlobalesJs() => LeerJs("atajos-globales.js");

    private static string LeerJs(string fichero)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "wwwroot", "js", fichero);
        File.Exists(ruta).Should().BeTrue($"{fichero} debería existir — si se movió o renombró, actualiza este test");
        return File.ReadAllText(ruta);
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
}
