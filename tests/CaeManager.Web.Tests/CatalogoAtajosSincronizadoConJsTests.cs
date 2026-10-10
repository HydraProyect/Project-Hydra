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
    /// Mismo riesgo para la sección «Dentro de una ficha»: <see cref="CatalogoAtajos.Ficha"/> anuncia
    /// las letras de <c>TECLAS_FICHA</c> de <c>atajos-ficha.js</c> y, aparte, el rango «1 – 9», que
    /// en el JS no es una lista sino la expresión <c>/^[1-9]$/</c>.
    /// </summary>
    [Fact]
    public void Teclas_del_js_de_ficha_coinciden_con_CatalogoAtajos_Ficha()
    {
        var contenidoJs = LeerJs("atajos-ficha.js");
        var match = Regex.Match(contenidoJs, @"TECLAS_FICHA\s*=\s*\[(?<teclas>[^\]]*)\]");

        match.Success.Should().BeTrue("atajos-ficha.js debe declarar TECLAS_FICHA como un array literal — si cambió de forma, actualiza este test");

        var teclasJs = Regex.Matches(match.Groups["teclas"].Value, @"'(\w+)'")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();

        var teclasCatalogo = CatalogoAtajos.Ficha
            .SelectMany(a => a.Tecla.Split(" / ", StringSplitOptions.TrimEntries))
            .ToHashSet();

        teclasCatalogo.Should().Contain("1 – 9", "la chuleta anuncia el cambio de pestaña por cifra");
        contenidoJs.Should().Contain("/^[1-9]$/", "es como atajos-ficha.js reconoce las cifras que la chuleta anuncia como «1 – 9»");
        teclasCatalogo.Remove("1 – 9");

        teclasJs.Should().BeEquivalentTo(teclasCatalogo,
            "la chuleta solo puede anunciar teclas de ficha que atajos-ficha.js reparte, y al revés");
    }

    /// <summary>
    /// Los atajos de ficha no tienen componente propio: viajan con el registro único de
    /// <c>atajos-globales.js</c>. Si ese registro deja de llamarlos, ninguna ficha tiene teclado y
    /// ningún test de componente lo nota.
    /// </summary>
    [Fact]
    public void Atajos_globales_registra_y_retira_los_atajos_de_ficha()
    {
        var contenidoJs = LeerAtajosGlobalesJs();

        contenidoJs.Should().Contain("registrarAtajosFicha()");
        contenidoJs.Should().Contain("atajosFicha.dispose()");
    }

    /// <summary>
    /// <c>atajos-ficha.js</c> trabaja sobre el DOM: reconoce la ficha, sus filas y sus botones por
    /// marcas y clases que pintan otros componentes. Un renombrado en cualquiera de ellos dejaría
    /// el teclado sin efecto y sin error; este test falla en su lugar.
    /// </summary>
    [Theory]
    [InlineData("SELECTOR_FICHA", "data-atajos-ficha", "Components/DesignSystem/CuerpoConLateral.razor")]
    [InlineData("SELECTOR_COLUMNA", "cuerpo-con-lateral-principal", "Components/DesignSystem/CuerpoConLateral.razor")]
    [InlineData("SELECTOR_LATERAL", "cuerpo-con-lateral-lateral", "Components/DesignSystem/CuerpoConLateral.razor")]
    [InlineData("SELECTOR_FILA", "data-pieza=\"fila\"", "Components/DesignSystem/FilaRelacion.razor")]
    [InlineData("SELECTOR_FILA", "fila-documento-requerido", "Features/Trabajadores/Pages/TrabajadorDetalle.razor")]
    [InlineData("SELECTOR_ACCION_DE_FILA", "fila-relacion-acciones", "Components/DesignSystem/FilaRelacion.razor")]
    [InlineData("SELECTOR_ACCION_DE_FILA", "accion-fila-enlace", "Features/Trabajadores/Pages/TrabajadorDetalle.razor")]
    [InlineData("SELECTOR_ACCION_DE_FILA", "tipo360-subfila-accion", "Features/Documentos/Pages/TipoDocumentoDetalle.razor")]
    [InlineData("SELECTOR_NO_ES_ACCION", "boton-360", "Components/DesignSystem/Boton360.razor")]
    [InlineData("SELECTOR_ACCIONES_CABECERA", "cabecera-identidad-acciones", "Components/DesignSystem/CabeceraIdentidad.razor")]
    [InlineData("SELECTOR_ACCIONES_CABECERA", "acciones-cabecera", "Components/DesignSystem/CabeceraPagina.razor")]
    public void Lo_que_el_js_de_ficha_busca_en_el_DOM_lo_pinta_algun_componente(string constante, string marca, string componente)
    {
        var declaracion = Regex.Match(LeerJs("atajos-ficha.js"), $@"const {constante}\s*=\s*(?<valor>[^;]+);");
        declaracion.Success.Should().BeTrue($"atajos-ficha.js debe declarar {constante}");
        declaracion.Groups["valor"].Value.Should().Contain(marca);

        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", componente);
        File.ReadAllText(ruta).Should().Contain(marca,
            $"{componente} es quien pinta «{marca}», que atajos-ficha.js busca con {constante}");
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
