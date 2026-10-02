using System.Text.RegularExpressions;
using FluentAssertions;
using static CaeManager.Architecture.Tests.ContrasteCss;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Salvaguarda de contraste de los componentes del sistema de diseño y de las
/// copias de su botón primario, en los dos temas. Es la ampliación que pidió el
/// seguimiento de #1037 y #1040: cada arreglo de contraste de esas dos PRs
/// encontró OTRO par con el mismo patrón al buscar en el árbol (el botón
/// flotante de avisos, y después el botón primario y sus ~20 copias), porque
/// los trinquetes anteriores miraban los pares que cada incremento ya conocía.
///
/// <para>
/// <b>Defecto de partida (medido 2026-10-02 en el árbol y en el navegador).</b>
/// <c>.boton-primario</c> pintaba letra blanca fija (<c>--color-neutral-0</c>)
/// sobre <c>--color-primary-500</c>, que el tema oscuro remapea a #5ca2f4:
/// 2,65:1; y su hover usaba <c>--color-primary-600</c> (remapeado a #bdd9ff):
/// 1,44:1. Lo copiaban Account, Reconnect, Asistente IA, el enlace de conectar
/// Microsoft, los chips activos, el día de hoy del calendario y el enlace de
/// saltar al contenido. La caja del editor de plantillas, sobre la página
/// blanca del PDF, quedaba en 1,18:1.
/// </para>
///
/// <para>
/// <b>Qué mide.</b> Tres cosas, cada una con su fallo propio:
/// </para>
/// <list type="number">
/// <item><see cref="Los_pares_de_la_lista_cumplen_su_minimo_en_el_tema"/>: la lista
/// EXPLÍCITA de pares (botones de todas las variantes en reposo y hover, badges,
/// chips, enlaces sobre superficie, copias del primario, caja de papel). Lee del
/// <c>.css</c> real el <c>background</c> y el <c>color</c>, resuelve los
/// <c>var()</c> con el bloque del tema por delante de <c>:root</c> y exige &gt;= 4,5:1.</item>
/// <item><see cref="Toda_variante_visual_de_los_componentes_del_sistema_esta_cubierta"/>:
/// toda clase <c>.boton-*</c>, <c>.badge-*</c> o <c>.chip-*</c> que declare color o fondo
/// tiene que estar en la lista. Una variante nueva sin cubrir hace fallar el test.</item>
/// <item><see cref="El_barrido_del_arbol_solo_encuentra_la_deuda_declarada"/>: barre TODAS
/// las hojas y exige que ninguna regla que declare fondo y letra resolubles baje de
/// 4,5:1 en un tema, salvo la deuda declarada abajo; esa lista, además, falla si una
/// entrada ya no incumple (se retira) o ya no existe. Es lo que habría cazado las
/// copias del primario sin que nadie las conociera.</item>
/// </list>
///
/// <para>
/// <b>Deuda declarada, no cerrada: el botón destructivo.</b> <c>--color-danger-500</c>
/// (#ef4444) con letra blanca da 3,76:1 en los DOS temas (también en claro). Arreglarlo
/// exige otro rojo de fondo, y eso cambia el tema claro: es decisión de producto
/// (sistema de diseño), no de este incremento. Mientras tanto la lista le fija un
/// mínimo de 3,7:1 para que no empeore, y el test de deuda falla en cuanto cumpla AA.
/// </para>
///
/// <para>
/// <b>Límites declarados.</b> Solo entiende <c>#rrggbb</c>, <c>var()</c> y
/// <c>color-mix(in srgb, C N%, transparent)</c>; un <c>rgba()</c>, <c>inherit</c> o
/// <c>oklch()</c> hace que el barrido omita esa regla (no puede medirla) y que la lista
/// explícita falle con mensaje. No mide opacidades (<c>opacity</c> sobre el texto),
/// degradados ni imágenes, ni el texto que hereda su color de un contexto. El barrido
/// une una regla con estado (<c>:hover</c>, <c>:active</c>, <c>:focus</c>) a la base del
/// MISMO fichero y mismo selector, no a una regla de otro fichero ni a una con
/// combinadores distintos. Y «sistema» no es un tercer caso: con
/// <c>prefers-color-scheme</c> desactivado resuelve a los valores de <c>:root</c>.
/// </para>
/// </summary>
public class ContrasteDeComponentesPorTemaTests
{
    private const double UmbralAa = 4.5;
    private const string Papel = "#ffffff";

    /// <param name="Minimo">4,5 salvo deuda declarada; ver el resumen de la clase.</param>
    /// <param name="FondoSiFalta">Fondo contra el que se mide cuando la regla no declara uno propio (texto sobre superficie).</param>
    /// <param name="Detras">Color sobre el que se compone un fondo translúcido (la página blanca del PDF).</param>
    private sealed record Par(
        string Nombre,
        string Fichero,
        string[] Selectores,
        double Minimo = UmbralAa,
        string? FondoSiFalta = null,
        string? Detras = null);

    private const string Boton = "Components/DesignSystem/Boton.razor.css";
    private const string Badge = "Components/DesignSystem/Badge.razor.css";
    private const string ListPage = "wwwroot/css/list-page.css";

    private static readonly Par[] Pares =
    [
        // ---- Botones del sistema, todas las variantes, en reposo y bajo el puntero ----
        new("Botón primario", Boton, [".boton-primario"]),
        new("Botón primario, hover", Boton, [".boton-primario", ".boton-primario:not(:disabled):hover"]),
        new("Botón secundario", Boton, [".boton-secundario"]),
        new("Botón secundario, hover", Boton, [".boton-secundario", ".boton-secundario:not(:disabled):hover"]),
        new("Botón fantasma", Boton, [".boton-fantasma"]),
        new("Botón fantasma, hover", Boton, [".boton-fantasma", ".boton-fantasma:not(:disabled):hover"]),
        // DEUDA (ver resumen): #ef4444 con letra blanca = 3,76:1 en claro y en oscuro.
        new("Botón destructivo (deuda: 3,76:1 en los dos temas)", Boton, [".boton-destructivo"], Minimo: 3.7),
        new("Botón destructivo, hover (deuda)", Boton, [".boton-destructivo", ".boton-destructivo:not(:disabled):hover"], Minimo: 3.7),
        new("Botón 360", "Components/DesignSystem/Boton360.razor.css", [".boton-360"]),
        new("Botón 360, hover", "Components/DesignSystem/Boton360.razor.css", [".boton-360", ".boton-360:hover"]),

        // ---- Badges ----
        new("Badge neutro", Badge, [".badge-neutro"]),
        new("Badge éxito", Badge, [".badge-exito"]),
        new("Badge advertencia", Badge, [".badge-advertencia"]),
        new("Badge peligro", Badge, [".badge-peligro"]),
        new("Badge info", Badge, [".badge-info"]),

        // ---- Chips ----
        new("Chip de filtro activo", ListPage, [".chip-filtro"]),
        new("Quitar del chip de filtro", ListPage, [".chip-filtro-quitar"], FondoSiFalta: "var(--color-surface-hover)"),
        new("Quitar del chip de filtro, hover", ListPage, [".chip-filtro-quitar", ".chip-filtro-quitar:hover"],
            FondoSiFalta: "var(--color-surface-hover)"),

        // ---- Enlaces ----
        new("Enlace sobre superficie", "wwwroot/css/base.css", ["a"], FondoSiFalta: "var(--color-surface)"),
        new("Enlace sobre el fondo de página", "wwwroot/css/base.css", ["a"], FondoSiFalta: "var(--color-bg)"),

        // ---- Copias del botón primario (mismo patrón, hoja propia) ----
        new("Primario de 2FA (Account)", "Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor.css",
            [".boton-primario"]),
        new("Primario de 2FA (Account), hover", "Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor.css",
            [".boton-primario", ".boton-primario:hover"]),
        new("Cerrar sesión de Pendiente de rol", "Components/Account/Pages/PendienteDeRol.razor.css",
            [".pendiente-rol-cerrar-sesion"]),
        new("Cerrar sesión de Pendiente de rol, hover", "Components/Account/Pages/PendienteDeRol.razor.css",
            [".pendiente-rol-cerrar-sesion", ".pendiente-rol-cerrar-sesion:hover"]),
        new("Círculo de paso completado", "Components/DesignSystem/IndicadorPasos.razor.css",
            [".indicador-pasos-completado .indicador-pasos-circulo"]),
        new("Saltar al contenido", "Components/Layout/MainLayout.razor.css", [".saltar-al-contenido"]),
        new("Reconectar", "Components/Layout/ReconnectModal.razor.css", ["#components-reconnect-modal button"]),
        new("Reconectar, hover", "Components/Layout/ReconnectModal.razor.css",
            ["#components-reconnect-modal button", "#components-reconnect-modal button:hover"]),
        new("Reconectar, activo", "Components/Layout/ReconnectModal.razor.css",
            ["#components-reconnect-modal button", "#components-reconnect-modal button:active"]),
        new("Mensaje del usuario (Asistente IA)", "Features/AsistenteIa/AsistenteIa.razor.css",
            [".asistente-mensaje-usuario"]),
        new("Enviar (Asistente IA)", "Features/AsistenteIa/AsistenteIa.razor.css", [".asistente-boton-enviar"]),
        new("Botón flotante del Asistente IA", "Features/AsistenteIa/BotonAsistenteIa.razor.css",
            [".boton-asistente-ia"]),
        new("Confirmar plan del Asistente IA", "Features/AsistenteIa/PlanAsistente.razor.css", [".plan-confirmar"]),
        new("Chip activo de la Bandeja", "Features/Bandeja/Pages/Bandeja.razor.css", [".bandeja-chip-activo"]),
        new("Chip activo de Mi trabajo", "Features/Bandeja/Pages/MiTrabajo.razor.css", [".mi-trabajo-chip-activo"]),
        new("Día de hoy del calendario", "Features/Calendario/Pages/Calendario.razor.css", [".calendario-dia-hoy"]),
        new("Paso actual de la revisión de sugerencia", "Features/Comunicaciones/Components/RevisionSugerenciaModal.razor.css",
            [".revision-stepper-actual .revision-stepper-circulo"]),
        new("Toggle activo de la Bandeja de Comunicaciones", "Features/Comunicaciones/Pages/Bandeja.razor.css",
            [".bandeja-toggle-activo:hover"]),
        new("Asa pulsada del orden del menú", "Features/Configuracion/Pages/OrdenMenuLateral.razor.css",
            [".orden-menu-asa[aria-pressed=\"true\"]"]),
        new("Primario del orden del menú", "Features/Configuracion/Pages/OrdenMenuLateral.razor.css",
            [".orden-menu-boton-primario"]),
        new("Conectar Microsoft", "Features/Integraciones/Pages/Conexiones.razor.css", [".enlace-conectar-microsoft"]),
        new("Conectar Microsoft, hover", "Features/Integraciones/Pages/Conexiones.razor.css",
            [".enlace-conectar-microsoft", ".enlace-conectar-microsoft:hover"]),
        new("Paso completado de la revisión de sugerencia", "Features/Comunicaciones/Components/RevisionSugerenciaModal.razor.css",
            [".revision-stepper-completado .revision-stepper-circulo"]),
        // Letra --color-surface (se invierte con el tema), por eso no usa el token del primario.
        new("Paso actual de Importación (letra de superficie)", "Features/Importacion/Pages/Importacion.razor.css",
            [".paso-importacion-actual"]),

        // ---- Caja del editor de plantillas sobre la página blanca del PDF ----
        new("Caja del editor de plantillas", "Features/Plantillas/Pages/ConfigurarPlantilla.razor.css",
            [".editor-plantilla-caja"], Detras: Papel),
        new("Caja del editor, seleccionada", "Features/Plantillas/Pages/ConfigurarPlantilla.razor.css",
            [".editor-plantilla-caja", ".editor-plantilla-caja-seleccionada"], Detras: Papel),
        new("Caja del editor, firma", "Features/Plantillas/Pages/ConfigurarPlantilla.razor.css",
            [".editor-plantilla-caja", ".editor-plantilla-caja-firma"], Detras: Papel),
        new("Asterisco de obligatorio de la caja", "Features/Plantillas/Pages/ConfigurarPlantilla.razor.css",
            [".editor-plantilla-caja-obligatorio"], FondoSiFalta: "var(--color-papel-caja-fondo)", Detras: Papel),
    ];

    /// <summary>
    /// Familias de componentes del sistema cuyas variantes visuales tienen que estar todas en
    /// <see cref="Pares"/>: fichero y patrón del selector base.
    /// </summary>
    private static readonly (string Fichero, string Patron)[] Familias =
    [
        (Boton, @"^\.boton-[\w-]+$"),
        ("Components/DesignSystem/Boton360.razor.css", @"^\.boton-360$"),
        (Badge, @"^\.badge-[\w-]+$"),
        (ListPage, @"^\.chip-[\w-]+$"),
    ];

    /// <summary>
    /// Reglas del barrido que hoy incumplen AA y no se arreglan aquí, con el mínimo que no
    /// pueden bajar. Clave: <c>fichero|selector</c> sin estados.
    /// </summary>
    private static readonly Dictionary<string, (double Minimo, string Motivo)> DeudaConocida = new(StringComparer.Ordinal)
    {
        ["Components/DesignSystem/Boton.razor.css|.boton-destructivo"] =
            (3.7, "#ef4444 con letra blanca, 3,76:1 en claro y en oscuro: otro rojo cambia el tema claro (decisión de producto)"),
        ["Components/Layout/MainLayout.razor.css|#blazor-error-ui"] =
            (3.7, "banda de error del framework, mismo rojo --color-danger-500 con letra blanca (3,76:1)"),
        ["wwwroot/app.css|.blazor-error-boundary"] =
            (3.7, "pantalla de error del framework, mismo rojo --color-danger-500 con letra blanca (3,76:1)"),
        ["Features/VigilanciaNormativa/PanelAvisosNormativos.razor.css|.badge-avisos-normativos"] =
            (3.7, "contador rojo del botón de avisos, mismo --color-danger-500 con letra blanca (3,76:1)"),
        ["Features/Retencion/Pages/Retencion.razor.css|.flujo-retencion-flecha"] =
            (1.3, "flecha decorativa del flujo (--color-border-strong sobre --color-surface-subtle): no lleva texto, es un trazo"),
    };

    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void Los_pares_de_la_lista_cumplen_su_minimo_en_el_tema(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        tokens.Completo.Should().BeTrue("tokens.css debe declarar :root y los bloques de tema oscuro y claro");

        var fallos = new List<string>();
        foreach (var par in Pares)
        {
            var (fondo, texto) = ColoresDeLaRegla(Leer(par.Fichero), par.Selectores);
            if (fondo is "none" or "transparent") fondo = null;
            fondo ??= par.FondoSiFalta;
            fondo.Should().NotBeNull($"{par.Nombre}: sin background propio ni FondoSiFalta no se puede medir el contraste");
            texto.Should().NotBeNull($"{par.Nombre}: sin color propio hereda un color que este test no ve");

            if (fondo!.Contains("color-mix", StringComparison.Ordinal))
                par.Detras.Should().NotBeNull($"{par.Nombre}: un fondo translúcido necesita el color que hay detrás (Detras)");

            var hexFondo = tokens.ResolverSobre(fondo, tema, par.Detras ?? Papel);
            var hexTexto = tokens.IntentarResolver(texto!, tema);
            hexFondo.Should().NotBeNull($"{par.Nombre}: formato de fondo no soportado por este test: '{fondo}'");
            hexTexto.Should().NotBeNull($"{par.Nombre}: formato de color no soportado por este test: '{texto}'");

            var ratio = Contraste(hexFondo!, hexTexto!);
            if (ratio < par.Minimo)
                fallos.Add($"{par.Nombre} ({par.Fichero}): {ratio:0.00}:1 en {tema}, mínimo {par.Minimo:0.0} " +
                           $"[fondo {fondo} = {hexFondo} / letra {texto} = {hexTexto}]");
        }

        string.Join("\n", fallos).Should().BeEmpty(
            "fondo y letra de cada par tienen que leerse (>= 4,5:1) en los dos temas; si salen de tokens que " +
            "no cambian de tema a la vez, usa un par con variante en tokens.css " +
            "(--color-primario-fondo / --color-primario-fondo-hover / --color-primario-texto para el primario)");
    }

    [Fact]
    public void La_deuda_de_la_lista_sigue_siendo_deuda()
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        var yaCumplen = new List<string>();

        foreach (var par in Pares.Where(p => p.Minimo < UmbralAa))
        {
            var (fondo, texto) = ColoresDeLaRegla(Leer(par.Fichero), par.Selectores);
            var peor = new[] { "oscuro", "claro" }.Min(t =>
                Contraste(tokens.ResolverSobre(fondo ?? par.FondoSiFalta!, t, par.Detras ?? Papel)!,
                    tokens.IntentarResolver(texto!, t)!));
            if (peor >= UmbralAa) yaCumplen.Add($"{par.Nombre}: {peor:0.00}:1");
        }

        yaCumplen.Should().BeEmpty("un par con mínimo rebajado que ya cumple AA vuelve a 4,5 (quita el Minimo)");
    }

    [Fact]
    public void Toda_variante_visual_de_los_componentes_del_sistema_esta_cubierta()
    {
        var cubiertas = Pares
            .SelectMany(p => p.Selectores.Select(s => (p.Fichero, Selector: SinEstados(s))))
            .ToHashSet();

        var sinCubrir = new List<string>();
        var vistas = 0;
        foreach (var (fichero, patron) in Familias)
        {
            foreach (var regla in Reglas(Leer(fichero)))
            {
                var baseSel = SinEstados(regla.Selector);
                if (!Regex.IsMatch(baseSel, patron)) continue;
                var (fondo, texto) = ColoresDelCuerpo(regla.Cuerpo);
                if (fondo is null && texto is null) continue;

                vistas++;
                if (!cubiertas.Contains((fichero, baseSel))) sinCubrir.Add($"{fichero}: {baseSel}");
            }
        }

        vistas.Should().BeGreaterThan(10, "sin variantes localizadas este test estaría en verde por no mirar nada");
        sinCubrir.Distinct().Should().BeEmpty(
            "una variante nueva de botón, badge o chip hay que añadirla a Pares (reposo y hover) para que se mida en " +
            "los dos temas");
    }

    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void El_barrido_del_arbol_solo_encuentra_la_deuda_declarada(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        var inesperados = new List<string>();
        var medidas = 0;

        foreach (var fichero in HojasDelArbol())
        {
            var rel = Path.GetRelativePath(RaizWeb(), fichero).Replace('\\', '/');
            foreach (var (clave, ratio) in Barrer(File.ReadAllText(fichero), rel, tokens, tema))
            {
                medidas++;
                if (ratio >= UmbralAa) continue;

                if (!DeudaConocida.TryGetValue(clave, out var deuda))
                    inesperados.Add($"{clave}: {ratio:0.00}:1 en {tema}");
                else if (ratio < deuda.Minimo)
                    inesperados.Add($"{clave}: {ratio:0.00}:1 en {tema} (la deuda declarada no puede bajar de {deuda.Minimo:0.0})");
            }
        }

        medidas.Should().BeGreaterThan(100, "sin reglas medidas este test estaría en verde por no mirar nada");
        inesperados.Distinct().Should().BeEmpty(
            "una regla con fondo y letra que no se lee (>= 4,5:1) en este tema: usa un par de tokens con variante " +
            "por tema (p. ej. --color-primario-fondo/-texto), o decláralo en DeudaConocida con el motivo");
    }

    [Fact]
    public void Cada_entrada_de_deuda_sigue_incumpliendo_en_algun_tema()
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        var incumplen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var fichero in HojasDelArbol())
        {
            var rel = Path.GetRelativePath(RaizWeb(), fichero).Replace('\\', '/');
            foreach (var tema in new[] { "oscuro", "claro" })
                foreach (var (clave, ratio) in Barrer(File.ReadAllText(fichero), rel, tokens, tema))
                    if (ratio < UmbralAa) incumplen.Add(clave);
        }

        DeudaConocida.Keys.Where(k => !incumplen.Contains(k)).Should().BeEmpty(
            "una regla de DeudaConocida que ya cumple AA (o desapareció) se retira de la lista para que la " +
            "excepción no quede muda");
    }

    // ---- Sensibilidad: el instrumento tiene que ver el defecto del primario ----

    private const string TokensDelDefecto = """
        :root {
          --color-primary-300: #5ca2f4;
          --color-primary-400: #2f6fdd;
          --color-primary-500: #235bc2;
          --color-primary-600: #1e4a9e;
          --color-neutral-0: #ffffff;
          --color-primario-fondo: var(--color-primary-500);
          --color-primario-fondo-hover: var(--color-primary-600);
        }
        :root[data-theme='oscuro'] {
          --color-primary-500: var(--color-primary-300);
          --color-primary-600: #bdd9ff;
        }
        :root[data-theme='claro'] {
          --color-primary-500: #235bc2;
        }
        """;

    [Fact]
    public void El_barrido_ve_el_primario_del_defecto_en_oscuro_y_su_hover_pero_no_en_claro()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        const string css = """
            .copia { background: var(--color-primary-500); color: var(--color-neutral-0); }
            .copia:hover { background: var(--color-primary-600); }
            """;

        var oscuro = Barrer(css, "x.css", tokens, "oscuro").Select(r => r.Ratio).ToList();
        var claro = Barrer(css, "x.css", tokens, "claro").Select(r => r.Ratio).ToList();

        oscuro.Should().HaveCount(2, "la base y el hover");
        oscuro.Min().Should().BeLessThan(1.5, "es el defecto del hover: #fff sobre #bdd9ff (1,44:1)");
        oscuro.Max().Should().BeApproximately(2.65, 0.01, "es el defecto de la base: #fff sobre #5ca2f4");
        claro.Min().Should().BeGreaterThan(UmbralAa, "control negativo: en claro el mismo par se lee en reposo y en hover");
    }

    [Fact]
    public void El_barrido_mide_un_hover_que_solo_cambia_el_fondo_con_la_letra_de_la_base()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        const string css = """
            .copia { background: var(--color-primary-400); color: var(--color-neutral-0); }
            .copia:hover { background: var(--color-primary-600); }
            """;

        var medidas = Barrer(css, "x.css", tokens, "oscuro").ToList();

        medidas.Should().HaveCount(2, "la base y el hover");
        medidas.Min(m => m.Ratio).Should().BeLessThan(1.5, "el hover remapeado a #bdd9ff con letra blanca (1,44:1)");
        medidas.Max(m => m.Ratio).Should().BeGreaterThan(UmbralAa, "la base (#2f6fdd) sí se lee: 4,73:1");
    }

    [Fact]
    public void El_instrumento_compone_un_fondo_translucido_sobre_el_papel()
    {
        var tokens = Tokens.Desde("""
            :root { --papel-fondo: color-mix(in srgb, #ffffff 55%, transparent); }
            :root[data-theme='oscuro'] { --color-surface: #17212c; }
            :root[data-theme='claro'] { --color-surface: #ffffff; }
            """);

        tokens.ResolverSobre("var(--papel-fondo)", "oscuro", "#000000").Should().Be("#8c8c8c", "255 x 0,55 = 140 (0x8c)");
        tokens.ResolverSobre("var(--papel-fondo)", "oscuro", Papel).Should().Be("#ffffff", "blanco sobre papel sigue siendo papel");
        tokens.ResolverSobre("rgba(1, 2, 3, .5)", "oscuro", Papel).Should().BeNull("un formato que no entiende no se inventa");
        tokens.IntentarResolver("inherit", "claro").Should().BeNull();
    }

    [Fact]
    public void Las_reglas_se_reparten_por_selector_y_los_estados_se_quitan()
    {
        var reglas = Reglas("""
            @media (max-width: 600px) { .a, .b:hover { color: #fff; } }
            .c:not(:disabled):hover { background: #000; }
            """).ToList();

        reglas.Select(r => r.Selector).Should().Equal(".a", ".b:hover", ".c:not(:disabled):hover");
        SinEstados(".c:not(:disabled):hover").Should().Be(".c");
        SinEstados("#components-reconnect-modal button:active").Should().Be("#components-reconnect-modal button");
    }

    // ---- Instrumento ----

    /// <summary>
    /// Para cada regla con fondo y letra resolubles (la propia o, si es de estado, completada con la
    /// base del mismo fichero y selector), la clave <c>fichero|selector sin estados</c> y su contraste.
    /// </summary>
    private static IEnumerable<(string Clave, double Ratio)> Barrer(string css, string fichero, Tokens tokens, string tema)
    {
        var reglas = Reglas(css).ToList();

        var bases = new Dictionary<string, (string? Fondo, string? Texto)>(StringComparer.Ordinal);
        foreach (var r in reglas.Where(r => SinEstados(r.Selector) == r.Selector))
        {
            var (f, t) = ColoresDelCuerpo(r.Cuerpo);
            var previo = bases.GetValueOrDefault(r.Selector);
            bases[r.Selector] = (f ?? previo.Fondo, t ?? previo.Texto);
        }

        foreach (var r in reglas)
        {
            var clave = SinEstados(r.Selector);
            var (fondo, texto) = ColoresDelCuerpo(r.Cuerpo);
            if (clave != r.Selector && bases.TryGetValue(clave, out var deBase))
            {
                fondo ??= deBase.Fondo;
                texto ??= deBase.Texto;
            }

            if (fondo is null || texto is null) continue;
            var hexFondo = tokens.IntentarResolver(fondo, tema);
            var hexTexto = tokens.IntentarResolver(texto, tema);
            if (hexFondo is null || hexTexto is null) continue;

            yield return ($"{fichero}|{clave}", Contraste(hexFondo, hexTexto));
        }
    }

    private static IEnumerable<string> HojasDelArbol()
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(RaizWeb(), "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}lib{sep}"))
            .Where(f => !f.EndsWith("tokens.css", StringComparison.Ordinal));
    }

    private static string Leer(string fichero)
    {
        var ruta = Path.Combine(RaizWeb(), fichero.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(ruta).Should().BeTrue($"{fichero} debe existir (lista de pares desfasada)");
        return File.ReadAllText(ruta);
    }
}
