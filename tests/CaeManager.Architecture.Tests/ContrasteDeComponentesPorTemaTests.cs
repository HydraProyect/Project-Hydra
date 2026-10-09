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
/// <b>Deuda declarada, no cerrada: rellenos de color semántico con letra blanca.</b>
/// <c>--color-danger-500</c> (#ef4444, botón destructivo, toast de error, banda de error del
/// framework, contador de avisos) da 3,76:1 con blanco en los DOS temas (también en claro);
/// <c>--color-success-500</c> (toast de éxito) 2,28:1 y <c>--color-warning-500</c> (toast de
/// advertencia) 2,15:1. Arreglarlos exige otro color de fondo (o letra oscura), y eso cambia el
/// tema claro: es decisión de producto (sistema de diseño), no de este incremento. Mientras
/// tanto cada par lleva un mínimo rebajado a su valor de hoy para que no empeore, y el test de
/// deuda falla en cuanto cumpla AA.
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
    private const string Toast = "Components/DesignSystem/AnfitrionToasts.razor.css";
    private const string Select = "Components/DesignSystem/CampoSelectAvanzado.razor.css";
    private const string Espera = "Components/DesignSystem/BotonConEspera.razor.css";

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

        // ---- Botón con espera (ficha 11): los estados con color propio. El relleno usa los colores de hover de cada variante, ya medidos arriba ----
        new("Botón con espera, aviso (colgada)", Espera, [".boton-espera ::deep .boton-espera-colgada"]),
        new("Botón con espera, aviso, hover", Espera,
            [".boton-espera ::deep .boton-espera-colgada", ".boton-espera ::deep .boton-espera-colgada:not(:disabled):hover"]),
        new("Botón con espera, hecha", Espera, [".boton-espera ::deep .boton-espera-hecha"]),
        new("Mensaje de la espera", Espera, [".boton-espera-mensaje"], FondoSiFalta: "var(--color-surface)"),
        new("Mensaje de la espera colgada", Espera, [".boton-espera[data-estado=\"colgada\"] .boton-espera-mensaje"],
            FondoSiFalta: "var(--color-surface)"),
        new("Cancelar de la espera", Espera, [".boton-espera-cancelar"], FondoSiFalta: "var(--color-surface)"),

        // ---- Toasts: la letra está en .toast y el fondo en la variante, por eso el barrido no los ve ----
        new("Toast informativo", Toast, [".toast", ".toast-info"]),
        // DEUDA (ver resumen): blanco sobre --color-success-500 (#22c55e) = 2,28:1 en los dos temas.
        new("Toast de éxito (deuda: 2,28:1 en los dos temas)", Toast, [".toast", ".toast-exito"], Minimo: 2.2),
        // DEUDA: blanco sobre --color-warning-500 (#f59e0b) = 2,15:1 en los dos temas.
        new("Toast de advertencia (deuda: 2,15:1 en los dos temas)", Toast, [".toast", ".toast-advertencia"], Minimo: 2.1),
        // DEUDA: blanco sobre --color-danger-500 (#ef4444) = 3,76:1.
        new("Toast de error (deuda: 3,76:1 en los dos temas)", Toast, [".toast", ".toast-error"], Minimo: 3.7),

        // ---- Badges ----
        new("Badge neutro", Badge, [".badge-neutro"]),
        new("Badge éxito", Badge, [".badge-exito"]),
        new("Badge advertencia", Badge, [".badge-advertencia"]),
        new("Badge peligro", Badge, [".badge-peligro"]),
        new("Badge tolerancia (En tolerancia)", Badge, [".badge-tolerancia"]),
        new("Badge info", Badge, [".badge-info"]),

        // ---- Chips ----
        new("Chip de filtro activo", ListPage, [".chip-filtro"]),
        new("Quitar del chip de filtro", ListPage, [".chip-filtro-quitar"], FondoSiFalta: "var(--color-surface-hover)"),
        new("Quitar del chip de filtro, hover", ListPage, [".chip-filtro-quitar", ".chip-filtro-quitar:hover"],
            FondoSiFalta: "var(--color-surface-hover)"),

        // ---- Listados (rediseño fase 1): filas tintadas por estado, pastilla activa, cabecera ----
        // La fila con problema es UNA regla (tabla, acordeón, cabecera de grupo y fichas 360) que termina en
        // .fila-degradado-*: el tono plano es el peor caso; el degradado solo lo aclara hacia --color-surface.
        new("Fila con problema, peligro (vencido, bloqueo o crítico)", ListPage, [".fila-degradado-peligro"]),
        new("Fila con problema, aviso (urgente)", ListPage, [".fila-degradado-aviso"]),
        new("Pastilla de filtro aplicada", "Components/DesignSystem/MenuAcciones.razor.css", [".menu-acciones-disparador-activa"]),
        new("Conmutador de selección múltiple activo", ListPage, [".cabecera-listado-icono-activo"]),
        new("Contador junto al título del listado", ListPage, [".cabecera-listado-contador"]),
        new("Cabecera de grupo del listado", ListPage, [".grupo-lista-cabecera"]),
        new("Contador de la cabecera de grupo", ListPage, [".grupo-lista-contador"], FondoSiFalta: "var(--color-surface)"),
        new("Resumen por estado de la cabecera de grupo", ListPage, [".grupo-lista-resumen"], FondoSiFalta: "var(--color-surface)"),

        // ---- Enlaces ----
        new("Enlace sobre superficie", "wwwroot/css/base.css", ["a"], FondoSiFalta: "var(--color-surface)"),
        new("Enlace sobre el fondo de página", "wwwroot/css/base.css", ["a"], FondoSiFalta: "var(--color-bg)"),

        // ---- Otros botones con hoja propia (las copias del primario se retiraron: ya usan <Boton>) ----
        new("Cerrar sesión de Pendiente de rol", "Components/Account/Pages/PendienteDeRol.razor.css",
            [".pendiente-rol-cerrar-sesion"]),
        new("Cerrar sesión de Pendiente de rol, hover", "Components/Account/Pages/PendienteDeRol.razor.css",
            [".pendiente-rol-cerrar-sesion", ".pendiente-rol-cerrar-sesion:hover"]),
        new("Círculo de paso completado", "Components/DesignSystem/IndicadorPasos.razor.css",
            [".indicador-pasos-completado .indicador-pasos-circulo"]),
        new("Saltar al contenido", "Components/Layout/MainLayout.razor.css", [".saltar-al-contenido"]),
        new("Contador de la campana de avisos", "Features/Notificaciones/CampanaAvisos.razor.css", [".campana-contador"]),
        new("Chip de vistas de demostración y vocabulario", "Components/Layout/PanelDesplegableCabecera.razor.css", [".vistas-chip"]),
        new("Aviso de la banda: sesión de soporte y fin de acceso", "Components/Layout/MainLayout.razor.css",
            [".cabecera-bandas ::deep [data-aviso-sistema][data-tono='soporte']"]),
        new("Aviso de la banda: solo consulta", "Components/Layout/MainLayout.razor.css",
            [".cabecera-bandas ::deep [data-aviso-sistema][data-tono='info']"]),
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
        new("Conectar Microsoft", "Features/Integraciones/Pages/Conexiones.razor.css", [".enlace-conectar-microsoft"]),
        new("Conectar Microsoft, hover", "Features/Integraciones/Pages/Conexiones.razor.css",
            [".enlace-conectar-microsoft", ".enlace-conectar-microsoft:hover"]),
        new("Paso completado de la revisión de sugerencia", "Features/Comunicaciones/Components/RevisionSugerenciaModal.razor.css",
            [".revision-stepper-completado .revision-stepper-circulo"]),
        // Letra --color-surface (se invierte con el tema), por eso no usa el token del primario.
        new("Paso actual de Importación (letra de superficie)", "Features/Importacion/Pages/Importacion.razor.css",
            [".paso-importacion-actual"]),

        // ---- Lista desplegable del kit (CampoSelectAvanzado): panel, opción activa, opción elegida y descripciones ----
        new("Opción del selector avanzado", Select, [".csa-panel"]),
        new("Opción activa del selector avanzado", Select, [".csa-panel", ".csa-activa"]),
        new("Opción elegida del selector avanzado", Select, [".csa-elegida"]),
        new("Descripción de una opción", Select, [".csa-descripcion"], FondoSiFalta: "var(--color-overlay)"),
        new("Descripción de la opción activa", Select, [".csa-activa", ".csa-descripcion"]),
        new("Marcador del disparador del selector avanzado", Select, [".csa-marcador"], FondoSiFalta: "var(--color-surface)"),
        new("Disparador del selector avanzado", Select, [".csa-disparador"]),

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
        (Toast, @"^\.toast-[\w-]+$"),
    ];

    /// <summary>Variantes de las familias que pintan color pero no llevan letra, con el motivo.</summary>
    private static readonly Dictionary<(string Fichero, string Selector), string> ExentasDeCobertura = new()
    {
        [(Toast, ".toast-progreso")] = "barra de cuenta atrás del toast: relleno sin letra",
    };

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

            if (EsTranslucido(tokens, fondo!, tema))
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

    /// <summary>
    /// «El tema claro queda idéntico» y la decisión de la coordinadora para oscuro, fijadas: un cambio que
    /// mantenga el contraste pero mueva estos valores tiene que ser deliberado, no un efecto lateral.
    /// </summary>
    [Fact]
    public void Los_tokens_del_primario_conservan_los_valores_decididos()
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));

        string R(string token, string tema) => tokens.IntentarResolver($"var({token})", tema)!;

        R("--color-primario-fondo", "claro").Should().Be("#235bc2", "claro no cambia: es el --color-primary-500 de siempre");
        R("--color-primario-fondo-hover", "claro").Should().Be("#1e4a9e", "claro no cambia: es el --color-primary-600 de siempre");
        R("--color-primario-texto", "claro").Should().Be("#ffffff");
        R("--color-exito-solido-texto", "claro").Should().Be("#ffffff");
        R("--color-primario-fondo", "oscuro").Should().Be("#2f6fdd", "decisión: primary-400, 4,73:1 con blanco");
        R("--color-primario-fondo-hover", "oscuro").Should().Be("#1e4a9e");
        R("--color-primario-texto", "oscuro").Should().Be("#ffffff");

        // «Sistema» (sin data-theme) resuelve a :root, no al bloque claro: se mide quitando los bloques de tema.
        var soloRaiz = Tokens.Desde(Regex.Replace(File.ReadAllText(RutaTokensCss()),
            @":root\[data-theme='\w+'\]\s*\{[^}]*\}", string.Empty));
        string Raiz(string token) => soloRaiz.IntentarResolver($"var({token})", "claro")!;

        Raiz("--color-primario-fondo").Should().Be("#235bc2", ":root («sistema») tiene que decir lo mismo que el bloque claro");
        Raiz("--color-primario-fondo-hover").Should().Be("#1e4a9e");
        Raiz("--color-primario-texto").Should().Be("#ffffff");
        Raiz("--color-exito-solido-texto").Should().Be("#ffffff");
    }

    /// <summary>
    /// El borde que identifica un control (DDL-062) tiene que distinguirse a 3:1 de toda superficie sobre la que puede pintarse.
    /// Hasta la paleta G (2026-10-09) el token valía lo mismo en los dos temas y el mínimo solo constaba en un comentario de
    /// <c>tokens.css</c>; desde que el oscuro lo redeclara, un retoque de ese bloque podía hundirlo sin que nada se pusiera en rojo.
    /// </summary>
    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void El_borde_de_control_se_distingue_a_3_a_1_de_cada_superficie(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        string R(string token) => tokens.IntentarResolver($"var({token})", tema)!;

        string[] superficies =
            ["--color-bg", "--color-surface", "--color-surface-subtle", "--color-surface-hover", "--color-elevated", "--color-overlay"];
        var insuficientes = superficies
            .Select(s => (Superficie: s, Ratio: Contraste(R("--color-border-control"), R(s))))
            .Where(m => m.Ratio < 3.0)
            .Select(m => $"{m.Superficie} {m.Ratio:0.00}:1")
            .ToList();

        insuficientes.Should().BeEmpty($"--color-border-control identifica un control y debe dar 3:1 en el tema {tema}");
    }

    /// <summary>
    /// «En tolerancia» (decisión de Chris, 2026-10-04) es un tono propio ENTRE el ámbar de advertencia y el rojo de peligro, en
    /// los dos temas y tanto en la letra como en el fondo del chip: el contraste ya lo mide <see cref="Pares"/>, esto fija que
    /// el tono no se funda con ninguno de sus vecinos (un retoque que lo acercase al ámbar o al rojo deshace la decisión).
    /// </summary>
    [Theory]
    [InlineData("oscuro", "--color-tolerancia-700", "--color-warning-700", "--color-danger-700")]
    [InlineData("claro", "--color-tolerancia-700", "--color-warning-700", "--color-danger-700")]
    [InlineData("oscuro", "--color-tolerancia-50", "--color-warning-50", "--color-danger-50")]
    [InlineData("claro", "--color-tolerancia-50", "--color-warning-50", "--color-danger-50")]
    public void El_tono_de_tolerancia_queda_entre_el_ambar_y_el_rojo_en_los_dos_temas(string tema, string tolerancia, string ambar, string rojo)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));

        double Matiz(string token)
        {
            var hex = tokens.IntentarResolver($"var({token})", tema);
            hex.Should().NotBeNull($"{token} debe declararse y resolverse en el tema {tema}");
            var n = Convert.ToInt32(hex![1..], 16);
            double r = (n >> 16) / 255.0, g = ((n >> 8) & 255) / 255.0, b = (n & 255) / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var d = max - Math.Min(r, Math.Min(g, b));
            var h = max == r ? ((g - b) / d) % 6 : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            // Matiz con signo alrededor del rojo (0°): un rojo que tira a carmesí (el --color-danger-50 de la paleta G, #341418) vale
            // -7,5° y no 352,5°. Sin el signo, el orden ámbar > tolerancia > rojo se rompe por la vuelta del círculo, no por el tono.
            var grados = (h * 60 + 360) % 360;
            return grados > 180 ? grados - 360 : grados;
        }

        var (mAmbar, mTolerancia, mRojo) = (Matiz(ambar), Matiz(tolerancia), Matiz(rojo));
        // 8° de margen: con 5° la letra de claro (#b45309, 26°) y un tolerancia-700 a 22° apenas se distinguen en un chip de 11 px.
        mTolerancia.Should().BeLessThan(mAmbar - 8, $"{tolerancia} no puede confundirse con el ámbar ({ambar})");
        mTolerancia.Should().BeGreaterThan(mRojo + 8, $"{tolerancia} no puede confundirse con el rojo ({rojo})");
    }

    /// <summary>
    /// Hallazgo de la revisión puente de «En tolerancia»: <c>Badge</c> y el punto de <c>CampoSelectAvanzado</c> derivan la clase CSS
    /// del NOMBRE del <c>TonoBadge</c>. Un valor nuevo sin regla caería al gris en silencio: este cruce lo hace fallar.
    /// </summary>
    [Fact]
    public void Todo_valor_de_TonoBadge_tiene_su_clase_en_el_Badge_y_en_el_punto_del_selector()
    {
        var badge = Leer(Badge);
        var selector = Leer(Select);
        var sinClase = new List<string>();

        foreach (var tono in Enum.GetValues<CaeManager.Web.Components.DesignSystem.TonoBadge>())
        {
            var nombre = tono.ToString().ToLowerInvariant();
            if (!Regex.IsMatch(badge, $@"(^|\s|,)\.badge-{nombre}\s*\{{")) sinClase.Add($".badge-{nombre} en {Badge}");
            // Neutro es el punto por defecto (.csa-punto, gris): no lleva variante.
            if (tono != CaeManager.Web.Components.DesignSystem.TonoBadge.Neutro
                && !Regex.IsMatch(selector, $@"(^|\s|,)\.csa-punto-{nombre}\s*\{{")) sinClase.Add($".csa-punto-{nombre} en {Select}");
        }

        sinClase.Should().BeEmpty("cada TonoBadge necesita su regla en las dos hojas que derivan la clase de su nombre");
    }

    /// <param name="Relleno">Regla cuyo <c>background</c> es el relleno que tiene que distinguirse (sin letra).</param>
    /// <param name="Contra">Regla del mismo fichero cuyo <c>background</c> es lo que hay detrás, o un <c>var()</c>.</param>
    private sealed record ParGrafico(string Nombre, string Fichero, string Relleno, string Contra);

    /// <summary>
    /// Rellenos sin letra, que WCAG 1.4.11 exige a 3:1 contra lo que tienen detrás: no entran en <see cref="Pares"/>
    /// (miden letra) ni en el barrido (exige fondo y letra en la misma regla). Dos defectos que vio la revisión del
    /// seguimiento de #1037/#1040: el bolo blanco del interruptor de Visitas sobre su pista (#5ca2f4 en oscuro) y los
    /// conectores de paso, que se quedaban en el azul claro mientras el círculo pasaba al primario con variante.
    /// </summary>
    private static readonly ParGrafico[] ParesGraficos =
    [
        new("Bolo del interruptor de Visitas sobre su pista", "Features/Visitas/Pages/Visitas.razor.css",
            ".visitas-interruptor:checked::before", ".visitas-interruptor:checked"),
        new("Conector de paso completado", "Components/DesignSystem/IndicadorPasos.razor.css",
            ".indicador-pasos-item.indicador-pasos-completado:not(:last-child)::after", "var(--color-surface)"),
        new("Punto de «En tolerancia» del selector avanzado sobre la superficie",
            "Components/DesignSystem/CampoSelectAvanzado.razor.css", ".csa-punto-tolerancia", "var(--color-surface)"),
        new("Línea completada de la revisión de sugerencia",
            "Features/Comunicaciones/Components/RevisionSugerenciaModal.razor.css",
            ".revision-stepper-linea-completa", "var(--color-surface)"),
    ];

    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void Los_rellenos_graficos_se_distinguen_de_lo_que_tienen_detras_a_3_a_1(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        var fallos = new List<string>();

        foreach (var par in ParesGraficos)
        {
            var css = Leer(par.Fichero);
            var (relleno, _) = ColoresDeLaRegla(css, [par.Relleno]);
            relleno.Should().NotBeNull($"{par.Nombre}: la regla no declara background");
            var contra = par.Contra.StartsWith("var(", StringComparison.Ordinal)
                ? par.Contra
                : ColoresDeLaRegla(css, [par.Contra]).Fondo;
            contra.Should().NotBeNull($"{par.Nombre}: lo que hay detrás no declara background");

            var hexRelleno = tokens.IntentarResolver(relleno!, tema);
            var hexContra = tokens.IntentarResolver(contra!, tema);
            hexRelleno.Should().NotBeNull($"{par.Nombre}: formato no soportado '{relleno}'");
            hexContra.Should().NotBeNull($"{par.Nombre}: formato no soportado '{contra}'");

            var ratio = Contraste(hexRelleno!, hexContra!);
            if (ratio < 3.0) fallos.Add($"{par.Nombre} ({par.Fichero}): {ratio:0.00}:1 en {tema} [{relleno} / {contra}]");
        }

        string.Join("\n", fallos).Should().BeEmpty(
            "un relleno gráfico (bolo, conector, pista) tiene que distinguirse >= 3:1 de lo que tiene detrás en los dos temas");
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
        var exentasConLetra = new List<string>();
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
                var exenta = ExentasDeCobertura.ContainsKey((fichero, baseSel));
                if (exenta && texto is not null) exentasConLetra.Add($"{fichero}: {baseSel}");
                if (!cubiertas.Contains((fichero, baseSel)) && !exenta)
                    sinCubrir.Add($"{fichero}: {baseSel}");
            }
        }

        vistas.Should().BeGreaterThan(10, "sin variantes localizadas este test estaría en verde por no mirar nada");
        exentasConLetra.Distinct().Should().BeEmpty(
            "una variante exenta porque no lleva letra ha ganado una: quítala de ExentasDeCobertura y añádela a Pares");
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

    /// <summary>
    /// Reglas con fondo y letra declarados que el barrido no sabe resolver (<c>rgba()</c>, degradados, un
    /// <c>var()</c> sin valor) y por tanto no mide. Medido el 2026-10-02. El tope solo baja: una regla nueva
    /// con un formato que el instrumento no entiende hace fallar el test en vez de entrar sin medir, y una
    /// que se arregla obliga a bajar la constante (la igualdad es estricta, para que el hueco liberado no lo
    /// ocupe otra regla sin medir). Se cuenta la unión de los dos temas.
    /// Las 9 de hoy: dos de la caja del editor (color-mix, medidas en <see cref="Pares"/> sobre el papel), cuatro de
    /// AccesoLayout (pantalla de acceso de tema fijo, con rgba), una de Importación (rgba sobre el paso actual,
    /// medida arriba con la letra de superficie) y dos iconos: uno toma el color de una propiedad local de Razor
    /// (<c>--estado-vacio-acento</c>) y el otro un tinte que no es #rrggbb (<c>--color-system-tint</c>).
    /// </summary>
    private const int OmitidasHoy = 9;

    [Fact]
    public void El_barrido_no_omite_en_silencio_mas_reglas_de_las_que_omite_hoy()
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        var omitidas = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var fichero in HojasDelArbol())
        {
            var rel = Path.GetRelativePath(RaizWeb(), fichero).Replace('\\', '/');
            foreach (var tema in new[] { "oscuro", "claro" })
                foreach (var (clave, ratio) in Evaluar(File.ReadAllText(fichero), rel, tokens, tema))
                    if (ratio is null) omitidas.Add(clave);
        }

        omitidas.Count.Should().Be(OmitidasHoy,
            "el barrido no sabe medir estas reglas (formato de color que no entiende): {0}. Si la regla es nueva, usa " +
            "#rrggbb o var(); si no se puede, súbela aquí con el motivo. Si bajó, baja la constante. Hoy omite {1}",
            string.Join("; ", omitidas), omitidas.Count);
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
        SinEstados(".fila:focus-within").Should().Be(".fila", ":focus-within es un estado, no se queda como «-within»");
        SinEstados(".fila:focus").Should().Be(".fila");
    }

    [Fact]
    public void Un_estado_focus_within_se_completa_con_la_letra_de_la_base()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        const string css = """
            .fila { background: var(--color-primary-400); color: var(--color-neutral-0); }
            .fila:focus-within { background: var(--color-primary-600); }
            """;

        var medidas = Barrer(css, "x.css", tokens, "oscuro").ToList();

        medidas.Should().HaveCount(2, "la base y el estado con foco dentro");
        medidas.Min(m => m.Ratio).Should().BeLessThan(1.5, "el estado con foco dentro se mide con la letra de la base");
    }

    [Fact]
    public void El_instrumento_entiende_important_y_el_fallback_de_var_y_los_cuenta_si_no_puede()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);

        ColoresDelCuerpo("background: #000 !important; color: var(--color-neutral-0) !important;")
            .Should().Be(("#000", "var(--color-neutral-0)"), "el !important no forma parte del valor");
        tokens.IntentarResolver("var(--no-existe, #123456)", "claro").Should().Be("#123456", "sin el token manda el fallback");
        tokens.IntentarResolver("var(--color-neutral-0, #123456)", "claro").Should().Be("#ffffff", "con el token declarado, el fallback no se usa");
        tokens.IntentarResolver("var(--no-existe)", "claro").Should().BeNull();

        var omitida = Evaluar(".x { background: rgba(0,0,0,.5); color: #fff; }", "x.css", tokens, "claro").Single();
        omitida.Ratio.Should().BeNull("una regla con un formato que no entiende se cuenta como omitida, no desaparece");
    }

    [Fact]
    public void La_guarda_de_fondo_translucido_ve_el_color_mix_que_llega_por_un_token()
    {
        var tokens = Tokens.Desde("""
            :root {
              --papel-fondo: color-mix(in srgb, #ffffff 55%, transparent);
              --solido: #336699;
            }
            :root[data-theme='oscuro'] { --x: #000000; }
            :root[data-theme='claro'] { --x: #000000; }
            """);

        EsTranslucido(tokens, "var(--papel-fondo)", "claro").Should().BeTrue("el color-mix llega por un var()");
        EsTranslucido(tokens, "var(--no-existe, color-mix(in srgb, #ffffff 55%, transparent))", "claro")
            .Should().BeTrue("el fallback también puede ser translúcido");
        EsTranslucido(tokens, "var(--solido)", "claro").Should().BeFalse("control negativo: un fondo opaco no necesita Detras");
    }

    // ---- Instrumento ----

    /// <summary>Translúcido = el color final depende de lo que haya detrás (también si el valor llega por un var()).</summary>
    private static bool EsTranslucido(Tokens tokens, string fondo, string tema) =>
        tokens.ResolverSobre(fondo, tema, "#ffffff") != tokens.ResolverSobre(fondo, tema, "#000000");

    /// <summary>
    /// Para cada regla con fondo y letra resolubles (la propia o, si es de estado, completada con la
    /// base del mismo fichero y selector), la clave <c>fichero|selector sin estados</c> y su contraste.
    /// </summary>
    private static IEnumerable<(string Clave, double Ratio)> Barrer(string css, string fichero, Tokens tokens, string tema) =>
        Evaluar(css, fichero, tokens, tema).Where(m => m.Ratio is not null).Select(m => (m.Clave, m.Ratio!.Value));

    /// <summary>Valores que no son un color concreto: no hay nada que medir, y no cuentan como regla omitida.</summary>
    private static readonly HashSet<string> SinColorConcreto = new(StringComparer.OrdinalIgnoreCase)
    {
        "none", "transparent", "inherit", "initial", "unset", "currentcolor",
    };

    /// <summary>
    /// Lo mismo que <see cref="Barrer"/>, pero también devuelve (con <c>Ratio</c> nulo) las reglas que declaran
    /// fondo y letra y que el instrumento no sabe resolver (<c>rgba()</c>, degradados, un <c>var()</c> sin
    /// valor...). Son las que el barrido omitiría en silencio.
    /// </summary>
    private static IEnumerable<(string Clave, double? Ratio)> Evaluar(string css, string fichero, Tokens tokens, string tema)
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
            if (SinColorConcreto.Contains(fondo) || SinColorConcreto.Contains(texto)) continue;
            var hexFondo = tokens.IntentarResolver(fondo, tema);
            var hexTexto = tokens.IntentarResolver(texto, tema);

            yield return ($"{fichero}|{clave}",
                hexFondo is null || hexTexto is null ? null : Contraste(hexFondo, hexTexto));
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
