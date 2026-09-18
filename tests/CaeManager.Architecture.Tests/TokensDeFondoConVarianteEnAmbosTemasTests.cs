using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo escalón <c>-50</c> de la paleta de color (el fondo claro de un chip de
/// estado: <c>--color-primary-50</c>, <c>--color-success-50</c>...) que se usa
/// como fondo en algún componente tiene que redeclararse en los dos bloques de
/// tema de <c>tokens.css</c>: <c>:root[data-theme='oscuro']</c> y
/// <c>:root[data-theme='claro']</c>.
///
/// <para>
/// <b>Por qué hace falta.</b> Medido el 2026-09-18: <c>--color-primary-50</c>
/// era el único escalón <c>-50</c> sin redefinir en oscuro, mientras que
/// <c>--color-secondary-50</c>, <c>--color-success-50</c>,
/// <c>--color-warning-50</c> y <c>--color-danger-50</c> sí lo estaban. El
/// resultado: en tema oscuro seguía pintando <c>#eef5ff</c> (fondo casi
/// blanco) mientras el texto que va encima sí se remapea a claro
/// (<c>--color-primary-700</c> → <c>--color-primary-100</c>), dejando texto
/// casi blanco sobre fondo casi blanco — el defecto reportado en
/// Configuración → Acceso e identidad → Roles, repetido en los otros 36 usos
/// de <c>--color-primary-50</c> como fondo del árbol. La otra prueba de esta
/// clase (<see cref="VariablesCssUsadasEstanDeclaradasTests"/>) no lo veía:
/// la variable SÍ estaba declarada, solo que en un único bloque en vez de en
/// los tres.
/// </para>
///
/// <para>
/// <b>Alcance deliberadamente estrecho.</b> Solo mira el escalón <c>-50</c>,
/// no toda la escala. Los escalones <c>-500</c>/<c>-600</c>/<c>-700</c> se
/// usan también como fondo (botones, badges sólidos, barras de alerta) pero
/// muchos de ellos no necesitan redefinición por tema — un rojo saturado de
/// alerta funciona igual sobre claro y oscuro cuando lleva texto blanco fijo
/// encima, y exigirles variante por tema habría dado falsos positivos sin
/// relación con el defecto real. El patrón <c>-50</c> es distinto: es
/// siempre un fondo casi blanco pensado como contenedor de texto de la misma
/// familia (<c>-500</c>/<c>-700</c>), y ese texto sí cambia de valor entre
/// temas — de ahí que el fondo tenga que acompañarlo.
/// </para>
/// </summary>
public class TokensDeFondoConVarianteEnAmbosTemasTests
{
    private static readonly Regex PatronDeclaracionEscalon50 =
        new(@"(--color-[a-zA-Z]+-50)\b\s*:", RegexOptions.Compiled);

    private static readonly Regex PatronDeclaracionConValor =
        new(@"(--color-[a-zA-Z]+-50)\b\s*:\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex PatronUsoComoFondo =
        new(@"background(-color)?\s*:\s*[^;]*?var\(\s*(--color-[a-zA-Z]+-50)\b", RegexOptions.Compiled);

    private static readonly Regex ComentarioCss = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    private sealed record UsoComoFondo(string Variable, string Fichero, int Linea);

    [Fact]
    public void Todo_escalon_50_usado_como_fondo_tiene_variante_en_oscuro_y_en_claro()
    {
        var tokensCss = File.ReadAllText(RutaTokensCss());
        var ficheros = Ficheros();
        ficheros.Should().NotBeEmpty(
            "sin ficheros localizados este trinquete estaría en verde por no mirar nada");

        var violaciones = Violaciones(tokensCss, ficheros)
            .Select(v => $"{v.Variable}  ({Relativa(v.Fichero)}:{v.Linea})")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal);

        string.Join("\n", violaciones).Should().BeEmpty(
            "un escalón -50 usado como fondo sin variante en los dos temas repite el defecto de "
            + "--color-primary-50 (2026-09-18): fondo casi blanco fijo bajo un texto que sí cambia de "
            + "color entre temas. Añade la redefinición que falte en tokens.css, en :root[data-theme="
            + "'oscuro'] y en :root[data-theme='claro']");
    }

    /// <summary>
    /// Prueba de sensibilidad: reconstruye el estado del 2026-09-18 (primary-50
    /// sin redefinir en ningún bloque de tema, usado como fondo en un
    /// componente) y comprueba que el detector lo marca. Si esto no falla, el
    /// trinquete de arriba no sirve de nada.
    /// </summary>
    [Fact]
    public void El_detector_marca_un_escalon_50_sin_redefinir_por_tema()
    {
        const string tokensConDefecto = """
            :root {
              --color-primary-50: #eef5ff;
              --color-primary-700: #163a7d;
              --color-secondary-50: #f1f4f7;
              --color-secondary-700: #1e2a38;
            }
            :root[data-theme='oscuro'] {
              --color-secondary-50: #1d2733;
              --color-secondary-700: #cbd5e1;
            }
            :root[data-theme='claro'] {
              --color-secondary-50: #f1f4f7;
              --color-secondary-700: #1e2a38;
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Entrada.razor.css", ".entrada-activa { background-color: var(--color-primary-50); }"),
        };

        var violaciones = Violaciones(tokensConDefecto, ficheros).ToList();

        violaciones.Select(v => v.Variable).Should().Equal(["--color-primary-50"],
            "primary-50 no está en ningún bloque de tema y sí se usa como fondo: es exactamente el defecto");
    }

    /// <summary>
    /// Control negativo: la misma mutación revertida (primary-50 SÍ redefinido
    /// en los dos temas, como secondary-50) no debe marcar nada. Sin este
    /// control, el detector de arriba podría estar en rojo por cualquier
    /// motivo — por ejemplo, marcando cualquier uso de -50 sin mirar los
    /// bloques de tema — y la prueba de sensibilidad pasaría por la razón
    /// equivocada.
    /// </summary>
    [Fact]
    public void El_detector_no_marca_un_escalon_50_redefinido_en_los_dos_temas()
    {
        const string tokensCorregido = """
            :root {
              --color-primary-50: #eef5ff;
              --color-primary-700: #163a7d;
            }
            :root[data-theme='oscuro'] {
              --color-primary-50: #0e1d39;
              --color-primary-700: #dcebff;
            }
            :root[data-theme='claro'] {
              --color-primary-50: #eef5ff;
              --color-primary-700: #163a7d;
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Entrada.razor.css", ".entrada-activa { background-color: var(--color-primary-50); }"),
        };

        Violaciones(tokensCorregido, ficheros).Should().BeEmpty();
    }

    /// <summary>
    /// Hallazgo de la revisión de Codex (2026-09-18) sobre la primera versión:
    /// cortar por posición de marcador en vez de por el cuerpo entre llaves
    /// atribuía al bloque de tema cualquier declaración situada entre los dos
    /// marcadores o después del último, aunque viviera en una regla distinta.
    /// Aquí <c>--color-primary-50</c> se "redefine" en <c>.regla-suelta</c>,
    /// una clase normal que cae justo entre los bloques oscuro y claro: si el
    /// detector mirase por posición la daría por buena para oscuro y este
    /// test pasaría en falso. Mirando el cuerpo entre llaves de cada bloque,
    /// ni oscuro ni claro la contienen y el defecto se sigue viendo.
    /// </summary>
    [Fact]
    public void El_detector_no_confunde_una_regla_ajena_entre_bloques_con_el_bloque_de_tema()
    {
        const string tokensConReglaSuelta = """
            :root {
              --color-primary-50: #eef5ff;
              --color-primary-700: #163a7d;
            }
            :root[data-theme='oscuro'] {
              --color-primary-700: #dcebff;
            }
            .regla-suelta {
              --color-primary-50: #0e1d39;
            }
            :root[data-theme='claro'] {
              --color-primary-700: #163a7d;
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Entrada.razor.css", ".entrada-activa { background-color: var(--color-primary-50); }"),
        };

        Violaciones(tokensConReglaSuelta, ficheros).Select(v => v.Variable).Should().Equal(["--color-primary-50"],
            "la declaración vive en .regla-suelta, no en ningún bloque de tema: sigue faltando en los dos");
    }

    /// <summary>
    /// Control de alcance: un escalón que NO es -50 (aquí -500), sin variante
    /// en ningún tema pero usado como fondo, no se marca. El alcance es
    /// deliberadamente estrecho (ver el resumen de la clase) y esta prueba es
    /// lo que distingue "estrecho a propósito" de "el regex no encontró
    /// nada".
    /// </summary>
    [Fact]
    public void El_detector_no_marca_escalones_distintos_de_50()
    {
        const string tokensCss = """
            :root {
              --color-danger-500: #ef4444;
            }
            :root[data-theme='oscuro'] {
            }
            :root[data-theme='claro'] {
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Alerta.razor.css", ".alerta { background-color: var(--color-danger-500); }"),
        };

        Violaciones(tokensCss, ficheros).Should().BeEmpty(
            "-500 no es el patrón de chip de estado que esta prueba vigila; exigirle variante por tema "
            + "sería un falso positivo sin relación con el defecto de origen");
    }

    private static IEnumerable<UsoComoFondo> Violaciones(
        string tokensCss, IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var (baseSet, oscuroSet, claroSet) = BloquesDeEscalon50(tokensCss);

        return ficheros
            .SelectMany(f => UsosComoFondoEn(f.Ruta, f.Contenido))
            .Where(u => baseSet.Contains(u.Variable))
            .Where(u => !oscuroSet.Contains(u.Variable) || !claroSet.Contains(u.Variable));
    }

    private static readonly Regex AperturaBloqueBase = new(@":root\s*\{", RegexOptions.Compiled);
    private static readonly Regex AperturaBloqueOscuro = new(@":root\[data-theme='oscuro'\]\s*\{", RegexOptions.Compiled);
    private static readonly Regex AperturaBloqueClaro = new(@":root\[data-theme='claro'\]\s*\{", RegexOptions.Compiled);

    /// <summary>
    /// Extrae el cuerpo delimitado por llaves de cada bloque de tema, no todo
    /// lo que hay entre un marcador y el siguiente: ninguno de los tres
    /// bloques anida llaves, así que la primera <c>}</c> tras la apertura
    /// cierra el bloque. Cortar por posición de marcador en vez de por llave
    /// (hallazgo de Codex, 2026-09-18) atribuiría al tema equivocado —o le
    /// daría por buena— una declaración que en realidad vive en otra regla
    /// entre medias o después del último bloque.
    /// </summary>
    private static (HashSet<string> Base, HashSet<string> Oscuro, HashSet<string> Claro) BloquesDeEscalon50(
        string tokensCss)
    {
        var texto = ComentarioCss.Replace(tokensCss, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

        var baseTexto = CuerpoDelBloque(texto, AperturaBloqueBase, ":root { ... }");
        var oscuroTexto = CuerpoDelBloque(texto, AperturaBloqueOscuro, ":root[data-theme='oscuro'] { ... }");
        var claroTexto = CuerpoDelBloque(texto, AperturaBloqueClaro, ":root[data-theme='claro'] { ... }");

        return (EscalonesConValorLiteralEn(baseTexto), EscalonesDeclaradosEn(oscuroTexto), EscalonesDeclaradosEn(claroTexto));
    }

    private static string CuerpoDelBloque(string texto, Regex apertura, string descripcion)
    {
        var m = apertura.Match(texto);
        m.Success.Should().BeTrue($"tokens.css debe declarar {descripcion}");

        var inicioCuerpo = m.Index + m.Length;
        var finCuerpo = texto.IndexOf('}', inicioCuerpo);
        finCuerpo.Should().BeGreaterThan(-1, $"{descripcion} no cierra con '}}'");

        return texto[inicioCuerpo..finCuerpo];
    }

    private static HashSet<string> EscalonesDeclaradosEn(string texto)
    {
        var declarados = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PatronDeclaracionEscalon50.Matches(texto))
            declarados.Add(m.Groups[1].Value);
        return declarados;
    }

    /// <summary>
    /// Un alias como <c>--color-info-50: var(--color-secondary-50);</c> no
    /// necesita variante propia: el valor cambia de tema a través de lo que
    /// referencia. Solo un valor literal (color, no <c>var(</c>) obliga a
    /// redefinirse en los dos bloques de tema.
    /// </summary>
    private static HashSet<string> EscalonesConValorLiteralEn(string texto)
    {
        var declarados = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PatronDeclaracionConValor.Matches(texto))
        {
            if (!m.Groups[2].Value.TrimStart().StartsWith("var(", StringComparison.Ordinal))
                declarados.Add(m.Groups[1].Value);
        }
        return declarados;
    }

    private static IEnumerable<UsoComoFondo> UsosComoFondoEn(string ruta, string contenido)
    {
        var texto = ruta.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            ? ComentarioCss.Replace(contenido, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()))
            : contenido;

        foreach (Match m in PatronUsoComoFondo.Matches(texto))
            yield return new UsoComoFondo(m.Groups[2].Value, ruta, texto[..m.Index].Count(c => c == '\n') + 1);
    }

    private static List<(string Ruta, string Contenido)> Ficheros()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        if (!Directory.Exists(raiz)) return [];

        string[] extensiones = [".css", ".razor.css"];
        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, "*", SearchOption.AllDirectories)
            .Where(f => extensiones.Any(ext => f.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Where(f => !f.Contains($"{separador}obj{separador}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{separador}bin{separador}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{separador}wwwroot{separador}lib{separador}", StringComparison.Ordinal))
            .Select(f => (Ruta: f, Contenido: File.ReadAllText(f)))
            .ToList();
    }

    private static string RutaTokensCss() =>
        Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "wwwroot", "css", "tokens.css");

    private static string Relativa(string ruta)
    {
        var raiz = RaizDelRepositorio();
        return ruta.StartsWith(raiz, StringComparison.OrdinalIgnoreCase) ? ruta[raiz.Length..].TrimStart('\\', '/') : ruta;
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
