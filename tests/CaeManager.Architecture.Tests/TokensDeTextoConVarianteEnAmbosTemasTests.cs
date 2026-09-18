using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Simétrico de <see cref="TokensDeFondoConVarianteEnAmbosTemasTests"/> para el
/// otro lado del par de contraste: todo escalón intermedio de la paleta
/// (<c>-300</c> a <c>-800</c>) que se use como <c>color:</c> de texto tiene que
/// resolver a un valor <b>distinto</b> en tema claro y en tema oscuro.
///
/// <para>
/// <b>Por qué hace falta.</b> El trinquete de #690 solo vigila los fondos
/// <c>-50</c>. Un color de texto fijo bajo un fondo que sí cambia rompe el
/// contraste en cuanto se conmuta el tema, y eso es lo que estaba pasando
/// (medido el 2026-09-18 sobre <c>ef42310c</c>, contraste WCAG 2.x sobre el
/// fondo declarado en la propia regla):
/// <list type="bullet">
///   <item><c>--color-neutral-800</c> en <c>.titulo-aviso-normativo</c>: 12,61:1 en claro, <b>1,29:1</b> en oscuro.</item>
///   <item><c>--color-neutral-700</c> en <c>.badge-neutro</c>: 7,82:1 en claro, <b>2,02:1</b> en oscuro.</item>
///   <item><c>--color-neutral-600</c> en <c>.descripcion-avisos-normativos</c>: 6,20:1 en claro, <b>2,62:1</b> en oscuro.</item>
///   <item><c>--color-info-500</c> en <c>.badge-info</c>, <c>.badge-visita</c>, <c>.pendiente-rol-icono</c> y cuatro reglas de Plataforma: 6,86–7,58:1 en claro, <b>1,99–2,15:1</b> en oscuro.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Resuelve la cadena de alias, y ahí mejora al trinquete de fondo.</b>
/// Aquél da por buena cualquier declaración cuyo valor sea <c>var(...)</c>,
/// razonando que el alias cambia a través de lo que referencia. Eso solo es
/// cierto si lo referenciado cambia de verdad: <c>--color-info-500</c> es
/// alias de <c>--color-secondary-500</c>, que era literal y no tenía variante
/// en ningún tema, así que el alias tampoco cambiaba — 7 usos como texto que
/// la regla del otro trinquete habría dejado pasar. Por eso aquí se compara
/// el <b>valor resuelto</b> en cada tema, no la forma de la declaración.
/// </para>
///
/// <para>
/// <b>Alcance: escalones -300 a -800.</b> Fuera quedan, con motivo medido, los
/// extremos <c>-0</c> y <c>-900</c> —blanco y casi negro, pensados como par
/// fijo sobre fondos sólidos: <c>--color-neutral-0</c> sobre
/// <c>--color-danger-500</c> da 3,76:1 en los dos temas por igual, y pedirle
/// variante no arreglaría nada— y los escalones claros <c>-50</c>, <c>-100</c>
/// y <c>-200</c>, que son fondos (el <c>-50</c> lo vigila ya el trinquete de
/// #690). Este trinquete mira <b>solo la forma</b> (¿cambia el token entre
/// temas?), nunca el contraste del par: resolver el fondo efectivo de una
/// regla exige la cascada CSS entera, y una heurística de regex sobre ella da
/// falsos positivos comprobados — <c>.toast</c> recibe el fondo de sus clases
/// modificadoras, no de la regla base.
/// </para>
/// </summary>
public class TokensDeTextoConVarianteEnAmbosTemasTests
{
    /// <summary>
    /// Uso como color de texto. <c>(?&lt;![-a-zA-Z])</c> descarta
    /// <c>background-color</c>, <c>border-color</c>, <c>caret-color</c> y
    /// compañía; <c>[^;{}]*?</c> con <c>\s</c> cruza saltos de línea, porque
    /// una declaración puede estar partida y una búsqueda por línea daría
    /// falsos negativos.
    /// </summary>
    private static readonly Regex PatronUsoComoTexto =
        new(@"(?<![-a-zA-Z])color\s*:\s*[^;{}]*?var\(\s*(--color-[a-zA-Z]+-[0-9]+)\b", RegexOptions.Compiled);

    private static readonly Regex PatronDeclaracion =
        new(@"(--color-[a-zA-Z0-9-]+)\s*:\s*([^;]+);", RegexOptions.Compiled);

    private static readonly Regex PatronAlias =
        new(@"^var\(\s*(--[a-zA-Z0-9-]+)\s*\)$", RegexOptions.Compiled);

    private static readonly Regex PatronEscalon =
        new(@"^--color-[a-zA-Z]+-([0-9]+)$", RegexOptions.Compiled);

    private static readonly Regex ComentarioCss =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// Escalones que este trinquete vigila. Ver el resumen de la clase para
    /// por qué quedan fuera -0, -50, -100, -200 y -900.
    /// </summary>
    private static readonly int[] EscalonesVigilados = [300, 400, 500, 600, 700, 800];

    /// <summary>
    /// Exenciones nombradas, cada una con su medida. No son "pendientes":
    /// son tokens para los que la variante por tema sería <b>incorrecta</b>, y
    /// el número que lo demuestra está al lado. Añadir una exención nueva
    /// exige el mismo par de cifras.
    /// </summary>
    private static readonly Dictionary<string, string> Exenciones = new(StringComparer.Ordinal)
    {
        ["--color-primary-400"] =
            "punto fijo del espejo de la familia primary. El bloque oscuro invierte la escala alrededor "
            + "del 400 (--color-primary-500 -> -300, -600 -> -200, -700 -> -100), así que la ausencia de "
            + "-400 no es un olvido sino la consecuencia del patrón. Su único uso como texto es el icono "
            + "de .zona-soltar-archivo-icono: 4,73:1 en claro y 3,44:1 en oscuro, por encima del 3:1 que "
            + "WCAG pide a un componente gráfico (no es texto de lectura).",

        ["--color-secondary-600"] =
            "par fijo con su fondo, en sus dos papeles. Como texto (.timeline-avatar y .bandeja-fila-avatar) "
            + "va sobre --color-secondary-100, que tampoco cambia entre temas: 8,53:1 en claro y en oscuro; "
            + "darle variante oscura sin dársela al fondo lo hundiría hasta ~1:1. Y a través de "
            + "--color-info-600 es el fondo sólido de .toast-info bajo texto --color-neutral-0 fijo: 10,51:1 "
            + "en los dos temas. El defecto adyacente —un avatar casi blanco sobre superficie oscura— es del "
            + "fondo -100 y va en su propio incremento.",

        ["--color-neutral-300"] =
            "en oscuro NO es el defecto que este trinquete persigue: 11,78:1 sobre --color-surface. Invertirlo "
            + "al espejo (-700) lo bajaría a 1,90:1, o sea cambiaría el tema en el que falla. Lo que falla es "
            + "el tema CLARO (1,38:1 en .estado-vacio-icono), y es un gris decorativo deliberado que comparte "
            + "token con la pista de AnilloCumplimiento y el borde de TarjetaMetrica: tocarlo es una decisión "
            + "de diseño contra el mockup, no de contraste por tema.",
    };

    /// <summary>
    /// Deuda congelada, no exenta: tokens que ya hacían de texto y de fondo en
    /// <c>origin/main</c> antes de esta PR, con variante por tema, y que por
    /// eso dejan el texto blanco fijo de sus fondos sólidos por debajo de
    /// 4.5:1 en oscuro. Medido el 2026-09-18 sobre <c>ef42310c</c> con
    /// <c>--color-neutral-0</c> encima.
    ///
    /// <para>
    /// No se arreglan aquí porque el alcance de V2 son los tokens de TEXTO y
    /// esto es un defecto de fondos sólidos que toca once componentes (Boton,
    /// ReconnectModal, AsistenteIa, Bandeja, Calendario, Conexiones,
    /// IndicadorPasos, ProgresoConMensajes, PaginaEstadoSistema, el stepper de
    /// revisión y la leyenda del donut). La lista está congelada: si crece,
    /// este test se pone en rojo.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DoblePapelCongelado = new(StringComparer.Ordinal)
    {
        ["--color-primary-500"] = "botón primario y chips activos: 6,27:1 en claro, 2,65:1 en oscuro",
        ["--color-primary-600"] = "hover del botón primario: 8,31:1 en claro, 1,44:1 en oscuro",
        ["--color-success-700"] = "círculo del stepper completado: 5,02:1 en claro, 1,74:1 en oscuro",
        ["--color-warning-700"] = "punto de la leyenda del donut: 5,02:1 en claro, 1,67:1 en oscuro",
    };

    private sealed record UsoComoTexto(string Variable, string Fichero, int Linea);

    [Fact]
    public void Todo_escalon_intermedio_usado_como_texto_resuelve_distinto_en_los_dos_temas()
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
            "un escalón -300..-800 usado como color de texto que resuelve al MISMO valor en los dos temas "
            + "deja el texto fijo bajo un fondo que sí cambia — el defecto de --color-neutral-800 "
            + "(1,29:1 en oscuro) y --color-info-500 (1,99:1) medido el 2026-09-18. Dale su variante en "
            + ":root[data-theme='oscuro'] usando un escalón que ya exista en su familia, o sustituye el uso "
            + "por el token semántico que corresponda (--color-text, --color-text-muted). Si la variante "
            + "fuese incorrecta, añádelo a Exenciones con las dos cifras de contraste que lo demuestren");
    }

    /// <summary>
    /// Prueba de sensibilidad sobre el árbol real: el detector tiene que ver
    /// como violación un token vigilado al que se le quita la variante oscura.
    /// Reconstruye el estado previo a esta PR para <c>--color-neutral-800</c>
    /// (1,29:1 en oscuro) sobre los ficheros de verdad, no sobre CSS de
    /// juguete. Si esto pasara, el trinquete de arriba estaría en verde por no
    /// mirar el árbol.
    /// </summary>
    [Fact]
    public void El_detector_marca_un_token_real_al_que_se_le_quita_la_variante_oscura()
    {
        var tokensSinVariante = SinDeclaracionEnBloqueOscuro(
            File.ReadAllText(RutaTokensCss()), "--color-neutral-800");

        var violaciones = Violaciones(tokensSinVariante, Ficheros()).ToList();

        violaciones.Select(v => v.Variable).Distinct().Should().Contain("--color-neutral-800",
            "sin su variante oscura, neutral-800 vuelve a resolver a #2a3441 en los dos temas, que es "
            + "exactamente el defecto de .titulo-aviso-normativo");
    }

    /// <summary>
    /// La mejora sobre el trinquete de fondo: un alias NO exime por sí mismo.
    /// <c>--color-info-500</c> es <c>var(--color-secondary-500)</c>; si la
    /// punta de esa cadena es literal y no cambia de tema, el alias tampoco
    /// cambia y el detector tiene que verlo. Era el caso real de los 7 usos de
    /// info-500 como texto.
    /// </summary>
    [Fact]
    public void El_detector_sigue_la_cadena_de_alias_hasta_el_valor_literal()
    {
        const string tokensConAliasQueNoCambia = """
            :root {
              --color-secondary-500: #475569;
              --color-info-500: var(--color-secondary-500);
            }
            :root[data-theme='oscuro'] {
            }
            :root[data-theme='claro'] {
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Badge.razor.css", ".badge-info { color: var(--color-info-500); }"),
        };

        Violaciones(tokensConAliasQueNoCambia, ficheros).Select(v => v.Variable).Should().Equal(["--color-info-500"],
            "el alias resuelve a #475569 en los dos temas: que la declaración tenga forma de var() no lo exime");
    }

    /// <summary>
    /// Control negativo del anterior: el mismo alias, cuando lo que referencia
    /// SÍ cambia de tema, no se marca. Sin este control, el test de arriba
    /// podría estar en rojo por marcar cualquier alias.
    /// </summary>
    [Fact]
    public void El_detector_no_marca_un_alias_cuya_punta_si_cambia_de_tema()
    {
        const string tokensConAliasQueSiCambia = """
            :root {
              --color-secondary-500: #475569;
              --color-info-500: var(--color-secondary-500);
            }
            :root[data-theme='oscuro'] {
              --color-secondary-500: #aeb9c7;
            }
            :root[data-theme='claro'] {
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Badge.razor.css", ".badge-info { color: var(--color-info-500); }"),
        };

        Violaciones(tokensConAliasQueSiCambia, ficheros).Should().BeEmpty(
            "en oscuro resuelve a #aeb9c7 y en claro a #475569: el alias sí cambia");
    }

    /// <summary>
    /// Control de alcance: los extremos y los escalones de fondo no se marcan
    /// aunque se usen como texto sin variante. <c>--color-neutral-0</c> sobre
    /// un botón sólido es el caso documentado en el resumen de la clase. Esta
    /// prueba distingue "estrecho a propósito" de "el regex no encontró nada".
    /// </summary>
    [Fact]
    public void El_detector_no_marca_los_extremos_ni_los_escalones_de_fondo()
    {
        const string tokensCss = """
            :root {
              --color-neutral-0: #ffffff;
              --color-neutral-900: #161e27;
              --color-primary-50: #eef5ff;
              --color-neutral-200: #e8edf2;
            }
            :root[data-theme='oscuro'] {
            }
            :root[data-theme='claro'] {
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Boton.razor.css", """
                .boton-primario { color: var(--color-neutral-0); }
                .chip { color: var(--color-neutral-900); }
                .pildora { color: var(--color-primary-50); }
                .nota { color: var(--color-neutral-200); }
                """),
        };

        Violaciones(tokensCss, ficheros).Should().BeEmpty(
            "-0, -900, -50 y -200 están fuera del alcance con motivo medido; marcarlos sería el falso "
            + "positivo que el trinquete de fondo ya evitó en #690");
    }

    /// <summary>
    /// El detector no puede confundir <c>background-color</c> —ni ninguna otra
    /// propiedad terminada en <c>-color</c>— con un uso como texto. Es la otra
    /// mitad del alcance: este trinquete es el simétrico del de fondo, no su
    /// duplicado.
    /// </summary>
    [Fact]
    public void El_detector_no_confunde_otras_propiedades_de_color_con_texto()
    {
        const string tokensCss = """
            :root {
              --color-neutral-700: #404d60;
            }
            :root[data-theme='oscuro'] {
            }
            :root[data-theme='claro'] {
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Tarjeta.razor.css", """
                .tarjeta { background-color: var(--color-neutral-700); }
                .tarjeta-borde { border-color: var(--color-neutral-700); }
                .tarjeta-cursor { caret-color: var(--color-neutral-700); }
                """),
        };

        Violaciones(tokensCss, ficheros).Should().BeEmpty(
            "ninguno de los tres es un color de texto");
    }

    /// <summary>
    /// El CSS servido no viene minificado, pero una declaración sí puede estar
    /// partida en varias líneas. Una búsqueda por línea daría aquí un falso
    /// negativo; el detector tiene que verlo igual.
    /// </summary>
    [Fact]
    public void El_detector_ve_una_declaracion_partida_en_varias_lineas()
    {
        const string tokensCss = """
            :root {
              --color-neutral-700: #404d60;
            }
            :root[data-theme='oscuro'] {
            }
            :root[data-theme='claro'] {
            }
            """;
        var ficheros = new List<(string Ruta, string Contenido)>
        {
            ("Partido.razor.css", ".badge {\n    color:\n        var(\n            --color-neutral-700\n        );\n}"),
        };

        Violaciones(tokensCss, ficheros).Select(v => v.Variable).Should().Equal(["--color-neutral-700"],
            "la declaración cruza cinco líneas: buscar por línea la perdería");
    }

    /// <summary>
    /// Ningún token vigilado puede ejercer a la vez de color de texto y de
    /// fondo, porque los dos papeles piden lo contrario al conmutar el tema y
    /// darle variante para arreglar uno rompe el otro.
    ///
    /// <para>
    /// No es una precaución teórica: la primera versión de esta PR espejó
    /// <c>--color-info-500</c> para arreglar sus 7 usos como texto sin ver que
    /// <c>.toast-info</c> lo usaba de fondo con <c>--color-neutral-0</c> fijo
    /// encima, y lo dejó en 1,99:1 — lo encontró la revisión de Codex, no la
    /// medición previa, que sí había contado ese uso y no lo miró. El arreglo
    /// fue partirlo en dos tokens por papel (<c>--color-info-600</c> para el
    /// fondo). Este test es lo que impide que vuelva a pasar.
    /// </para>
    /// </summary>
    [Fact]
    public void Ningun_token_vigilado_hace_de_texto_y_de_fondo_a_la_vez()
    {
        var ficheros = Ficheros();
        ficheros.Should().NotBeEmpty();

        var comoTexto = ficheros
            .SelectMany(f => UsosComoTextoEn(f.Ruta, f.Contenido))
            .Select(u => u.Variable)
            .Where(EsEscalonVigilado)
            .ToHashSet(StringComparer.Ordinal);

        var ambosPapeles = ficheros
            .SelectMany(f => UsosComoFondoEn(f.Contenido).Select(v => (Variable: v, f.Ruta)))
            .Where(u => comoTexto.Contains(u.Variable))
            .Where(u => !DoblePapelCongelado.ContainsKey(u.Variable))
            .Select(u => $"{u.Variable}  (fondo en {Relativa(u.Ruta)})")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal);

        string.Join("\n", ambosPapeles).Should().BeEmpty(
            "un token que hace de texto y de fondo no puede tener una variante por tema correcta para los "
            + "dos papeles: el texto quiere aclararse en oscuro y el fondo quiere seguir oscuro. Pártelo en "
            + "dos tokens por papel, como --color-info-500 (texto) y --color-info-600 (fondo de .toast-info)");
    }

    /// <summary>
    /// El trinquete solo baja: una entrada congelada que ya no hace de texto y
    /// de fondo a la vez está arreglada y tiene que salir de la lista, o la
    /// lista deja de medir nada y se convierte en decoración.
    /// </summary>
    [Fact]
    public void Ninguna_entrada_de_deuda_congelada_sobra()
    {
        var ficheros = Ficheros();
        ficheros.Should().NotBeEmpty();

        var comoTexto = ficheros
            .SelectMany(f => UsosComoTextoEn(f.Ruta, f.Contenido))
            .Select(u => u.Variable)
            .ToHashSet(StringComparer.Ordinal);
        var comoFondo = ficheros
            .SelectMany(f => UsosComoFondoEn(f.Contenido))
            .ToHashSet(StringComparer.Ordinal);

        var sobrantes = DoblePapelCongelado.Keys
            .Where(t => !comoTexto.Contains(t) || !comoFondo.Contains(t))
            .OrderBy(x => x, StringComparer.Ordinal);

        string.Join("\n", sobrantes).Should().BeEmpty(
            "ya no ejerce los dos papeles: bórralo de DoblePapelCongelado para que el trinquete no pueda "
            + "volver a subir hasta ahí");
    }

    private static readonly Regex PatronUsoComoFondo =
        new(@"background(-color)?\s*:\s*[^;{}]*?var\(\s*(--color-[a-zA-Z]+-[0-9]+)\b", RegexOptions.Compiled);

    private static IEnumerable<string> UsosComoFondoEn(string contenido)
    {
        foreach (Match m in PatronUsoComoFondo.Matches(SinComentarios(contenido)))
            yield return m.Groups[2].Value;
    }

    /// <summary>
    /// Las exenciones tienen que seguir siendo necesarias. Una exención sobre
    /// un token que ya no se usa como texto, o que ya tiene variante, es
    /// ruido que tapa el siguiente defecto: se borra en cuanto sobra.
    /// </summary>
    [Fact]
    public void Ninguna_exencion_sobra()
    {
        var tokensCss = File.ReadAllText(RutaTokensCss());
        var ficheros = Ficheros();
        var (declaraciones, usados) = Contexto(tokensCss, ficheros);

        var sobrantes = Exenciones.Keys
            .Where(t => !usados.Contains(t) || Cambia(t, declaraciones))
            .OrderBy(x => x, StringComparer.Ordinal);

        string.Join("\n", sobrantes).Should().BeEmpty(
            "una exención cuyo token ya no se usa como texto, o que ya resuelve distinto en cada tema, "
            + "solo sirve para tapar el siguiente defecto: bórrala de Exenciones");
    }

    private static IEnumerable<UsoComoTexto> Violaciones(
        string tokensCss, IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var (declaraciones, _) = Contexto(tokensCss, ficheros);

        return ficheros
            .SelectMany(f => UsosComoTextoEn(f.Ruta, f.Contenido))
            .Where(u => EsEscalonVigilado(u.Variable))
            .Where(u => !Exenciones.ContainsKey(u.Variable))
            .Where(u => !Cambia(u.Variable, declaraciones));
    }

    private static (Declaraciones Declaraciones, HashSet<string> UsadosComoTexto) Contexto(
        string tokensCss, IEnumerable<(string Ruta, string Contenido)> ficheros)
    {
        var usados = new HashSet<string>(
            ficheros.SelectMany(f => UsosComoTextoEn(f.Ruta, f.Contenido)).Select(u => u.Variable),
            StringComparer.Ordinal);

        return (DeclaracionesDe(tokensCss), usados);
    }

    private static bool EsEscalonVigilado(string variable)
    {
        var m = PatronEscalon.Match(variable);
        return m.Success && EscalonesVigilados.Contains(int.Parse(m.Groups[1].Value));
    }

    /// <summary>
    /// Compara el valor <b>resuelto</b> en cada tema siguiendo la cadena de
    /// alias. Un token que no se resuelve en alguno de los dos temas (valor no
    /// literal, cadena rota) se trata como "no cambia": el trinquete prefiere
    /// un falso positivo visible a un hueco silencioso.
    /// </summary>
    private static bool Cambia(string variable, Declaraciones declaraciones)
    {
        var claro = Resolver(variable, declaraciones.Claro, declaraciones.Base);
        var oscuro = Resolver(variable, declaraciones.Oscuro, declaraciones.Base);
        return claro is not null && oscuro is not null
            && !string.Equals(claro, oscuro, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Resolver(
        string variable, Dictionary<string, string> tema, Dictionary<string, string> baseTema, int profundidad = 0)
    {
        if (profundidad > 10) return null;

        if (!tema.TryGetValue(variable, out var valor) && !baseTema.TryGetValue(variable, out valor))
            return null;

        valor = valor.Trim();
        var alias = PatronAlias.Match(valor);
        return alias.Success
            ? Resolver(alias.Groups[1].Value, tema, baseTema, profundidad + 1)
            : valor;
    }

    private sealed record Declaraciones(
        Dictionary<string, string> Base,
        Dictionary<string, string> Oscuro,
        Dictionary<string, string> Claro);

    private static readonly Regex AperturaBloqueBase = new(@":root\s*\{", RegexOptions.Compiled);
    private static readonly Regex AperturaBloqueOscuro = new(@":root\[data-theme='oscuro'\]\s*\{", RegexOptions.Compiled);
    private static readonly Regex AperturaBloqueClaro = new(@":root\[data-theme='claro'\]\s*\{", RegexOptions.Compiled);

    private static Declaraciones DeclaracionesDe(string tokensCss)
    {
        var texto = SinComentarios(tokensCss);
        return new Declaraciones(
            DeclaracionesEn(CuerpoDelBloque(texto, AperturaBloqueBase, ":root { ... }")),
            DeclaracionesEn(CuerpoDelBloque(texto, AperturaBloqueOscuro, ":root[data-theme='oscuro'] { ... }")),
            DeclaracionesEn(CuerpoDelBloque(texto, AperturaBloqueClaro, ":root[data-theme='claro'] { ... }")));
    }

    /// <summary>
    /// Igual que en el trinquete de fondo: se corta por el cuerpo entre llaves,
    /// no por la posición del marcador, para no atribuir a un bloque de tema
    /// una declaración que vive en otra regla entre medias (hallazgo de Codex,
    /// 2026-09-18).
    /// </summary>
    private static string CuerpoDelBloque(string texto, Regex apertura, string descripcion)
    {
        var m = apertura.Match(texto);
        m.Success.Should().BeTrue($"tokens.css debe declarar {descripcion}");

        var inicioCuerpo = m.Index + m.Length;
        var finCuerpo = texto.IndexOf('}', inicioCuerpo);
        finCuerpo.Should().BeGreaterThan(-1, $"{descripcion} no cierra con '}}'");

        return texto[inicioCuerpo..finCuerpo];
    }

    private static Dictionary<string, string> DeclaracionesEn(string texto)
    {
        var declaraciones = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in PatronDeclaracion.Matches(texto))
            declaraciones[m.Groups[1].Value] = m.Groups[2].Value.Trim();
        return declaraciones;
    }

    /// <summary>
    /// Mutación para la prueba de sensibilidad: borra la declaración de
    /// <paramref name="variable"/> del bloque oscuro dejando el resto intacto.
    /// </summary>
    private static string SinDeclaracionEnBloqueOscuro(string tokensCss, string variable)
    {
        var m = AperturaBloqueOscuro.Match(tokensCss);
        m.Success.Should().BeTrue("tokens.css debe declarar :root[data-theme='oscuro']");

        var inicio = m.Index + m.Length;
        var fin = tokensCss.IndexOf('}', inicio);
        var cuerpo = tokensCss[inicio..fin];

        var sinDeclaracion = Regex.Replace(
            cuerpo, $@"{Regex.Escape(variable)}\s*:[^;]+;", string.Empty);
        sinDeclaracion.Should().NotBe(cuerpo,
            $"la mutación debe borrar algo: si {variable} no estaba en el bloque oscuro, esta prueba de "
            + "sensibilidad no estaría midiendo nada");

        return tokensCss[..inicio] + sinDeclaracion + tokensCss[fin..];
    }

    private static IEnumerable<UsoComoTexto> UsosComoTextoEn(string ruta, string contenido)
    {
        var texto = SinComentarios(contenido);

        foreach (Match m in PatronUsoComoTexto.Matches(texto))
            yield return new UsoComoTexto(m.Groups[1].Value, ruta, texto[..m.Index].Count(c => c == '\n') + 1);
    }

    /// <summary>
    /// Sustituye cada comentario por espacios conservando los saltos de línea,
    /// para que los números de línea reportados sigan siendo los del fichero.
    /// </summary>
    private static string SinComentarios(string texto) =>
        ComentarioCss.Replace(texto, m => new string(m.Value.Select(c => c == '\n' ? '\n' : ' ').ToArray()));

    private static List<(string Ruta, string Contenido)> Ficheros()
    {
        var raiz = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");
        if (!Directory.Exists(raiz)) return [];

        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(raiz, "*.css", SearchOption.AllDirectories)
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
