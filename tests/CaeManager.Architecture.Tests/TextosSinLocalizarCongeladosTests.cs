using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Trinquete de la migración de textos de interfaz a recursos <c>.resx</c>
/// (localización es-ES/ca-ES). Cuenta, por superficie de <c>CaeManager.Web</c>
/// —cada carpeta de <c>Features/</c>, cada carpeta de <c>Components/</c> y cada
/// carpeta suelta de Web—, las cadenas <b>distintas</b> de texto natural que
/// siguen escritas a mano en <c>.razor</c> y <c>.cs</c> en vez de salir de un
/// <c>IStringLocalizer</c>.
///
/// <para>
/// <b>Igualdad, no techo</b>: el recuento de cada superficie tiene que coincidir
/// con <see cref="Congelado"/>. Si sube, se ha escrito texto nuevo sin
/// localizar; si baja, se ha migrado texto y la cifra se baja en el mismo
/// commit, para que el trinquete apriete y lo migrado no pueda volver. Una
/// superficie que no aparece en el diccionario tiene techo 0: una Feature nueva
/// nace localizada.
/// </para>
///
/// <para>
/// <b>Qué ve y qué no</b> (heurística léxica, no semántica; medida sobre el
/// árbol en el inventario del incremento): atributos de texto conocidos del
/// DesignSystem y de HTML (<c>Etiqueta="…"</c>, <c>aria-label="…"</c>…), texto
/// entre etiquetas de markup, y literales C# que parecen lenguaje natural
/// (tilde, «¿¡», palabra vacía seguida de palabra, o dos palabras con la
/// primera en mayúscula) más las etiquetas de una palabra en brazos de
/// <c>switch</c>, ternarios y <c>return</c>. Excluye a propósito las
/// sentencias con <c>throw new …Exception</c>, <c>Log*</c>, <c>logger.</c>,
/// <c>Console.</c> y <c>Regex</c>: no son interfaz. No ve una palabra suelta
/// en minúscula, ni textos que llegan de datos (catálogos sembrados, mensajes
/// de <c>Result</c> de Application, excepciones de Domain): esos tienen su
/// propio incremento. Tras migrar, el español no puede quedarse como valor por
/// defecto dentro de la llamada (<c>T["Clave", "Guardar"]</c>): seguiría
/// contando, y es lo correcto.
/// </para>
/// </summary>
public class TextosSinLocalizarCongeladosTests
{
    /// <summary>
    /// Cadenas distintas sin localizar por superficie. Solo baja: cada PR de
    /// migración de una Feature pone aquí su cifra nueva (0 si la termina).
    /// </summary>
    private static readonly Dictionary<string, int> Congelado = new(StringComparer.Ordinal)
    {
        ["ApiKeys"] = 45,
        ["Auditoria"] = 73,
        ["AuditoriaIa"] = 50,
        ["Bandeja"] = 107,
        ["Blindaje42"] = 46,
        ["BusquedaGlobal"] = 72,
        ["Centros"] = 314,
        ["Clientes"] = 278,
        ["Comercial"] = 61,
        ["Components/Account"] = 95,
        ["Components/DesignSystem"] = 52,
        // 92 → 77 el 2026-09-23 SIN migrar nada: los rótulos del menú lateral pasaron del marcado
        // de NavMenu.razor a literales de CatalogoMenuLateral.cs, y la heurística no ve un literal
        // de una sola palabra («Dashboard», «Empresas»…). Siguen sin localizar; su migración a
        // .resx es un incremento pendiente, no algo que esta cifra certifique.
        ["Components/Layout"] = 77,
        ["Components/Legal"] = 163,
        ["Components/Pages"] = 18,
        ["Components/Workspace"] = 58,
        ["Comunicaciones"] = 316,
        // Migrada a TextosConfiguracion: el 1 restante es un falso positivo del
        // detector, la cabecera «@for (var indice = 0; indice < Grupos.Count; …)»
        // de Configuracion.razor, que el '<' de la comparación hace pasar por texto.
        ["Configuracion"] = 1,
        ["Cumplimiento"] = 11,
        ["Dashboard"] = 57,
        // 89 → 1 el 2026-09-23 al migrar la Feature a TextosDashboardEjecutivo.resx. El 1 que
        // queda NO es texto: es un falso positivo del detector de markup, que toma por texto lo
        // que hay entre el «>» de <CampoSelect …> y el «<» del genérico de
        // «@foreach (var preset in Enum.GetValues<PresetPeriodoKpi>())». No se reescribe el
        // bucle para esquivar la heurística; si el detector aprende a ignorar código Razor,
        // esta entrada se retira.
        ["DashboardEjecutivo"] = 1,
        ["Delegaciones"] = 97,
        ["Documentos"] = 459,
        ["Empresas"] = 204,
        ["Extension"] = 29,
        ["Facturacion"] = 96,
        ["GestionRoles"] = 52,
        // 166 → 15 el 2026-09-23 al migrar la Feature a TextosImportacion.resx. Ninguno de los 15
        // es interfaz pendiente:
        // - 9 son CONTRATO del archivo, no interfaz: rótulos de columna que escribe GenerarPlantilla
        //   y lee el parser («Razón social», «Crítico (C/N)», «Dirección», «Código», «Contrato
        //   vigente hasta», «Fecha de nacimiento», «Crítico») y la fila de ejemplo de la plantilla
        //   de Clientes («Calle Ejemplo 1, Ciudad», «Nombre Apellidos — email@ejemplo.com»).
        //   Localizarlos rompería la importación en ca-ES; los Web.Tests los comparan con la
        //   plantilla generada.
        // - 1 se persiste: «Excepción no controlada durante la importación.» va a
        //   HistorialImportacion.MensajeError; es dato, no se localiza.
        // - 5 son falsos positivos del detector de markup: código Razor entre «>» y «<»
        //   («(var i = 0; i», «(numero», «.ToString("dd/MM/yy HH:mm")») y los corchetes anidados de
        //   @Textos[ClavesPasos[i]] y @Textos["BotonContinuarConPlantilla", Textos[…].Value].
        ["Importacion"] = 15,
        ["Integraciones"] = 92,
        ["Plantillas"] = 158,
        ["Plataforma"] = 74,
        ["Retencion"] = 85,
        ["Subcontratas"] = 225,
        // 149 → 6 el 2026-09-23 al migrar la Feature a TextosTiposDocumento.resx. Quedan:
        // «ITA», «RNT» y «RLC», las siglas oficiales de las opciones de PerfilDocumentoOficial
        // (nombre propio del documento de la Administración, igual en cualquier idioma: no se
        // localizan), y 3 falsos positivos del detector de markup, que toma por texto lo que hay
        // entre el «>» de <CampoSelect …> y el «<» del genérico de
        // «ValorChanged="v => _ambito = Enum.Parse<AmbitoAplicacion>(v)"» (y los de _requerido
        // y _naturaleza). No se reescriben las lambdas para esquivar la heurística.
        ["TiposDocumento"] = 6,
        ["Trabajadores"] = 193,
        ["Usuarios"] = 157,
        // 112 → 10 el 2026-09-23: Visitas.razor(.cs) migrados a TextosVisitas.resx. Los 10
        // que quedan son las etiquetas estáticas de NivelUrgenciaVisitaUi y AntelacionVisitaUi,
        // que también pintan Dashboard (Inicio) y DashboardEjecutivo: migrarlas cambia la firma
        // de helpers compartidos entre Features y es un incremento propio.
        ["Visitas"] = 10,
        ["Web(raiz)"] = 4,
        ["Web/Api"] = 2,
        ["Web/Reportes"] = 5,
        ["Web/Services"] = 2,
    };

    [Fact]
    public void Los_textos_sin_localizar_no_crecen_y_lo_migrado_aprieta_el_trinquete()
    {
        var medido = DetectorTextosSinLocalizar.MedirWeb(RaizWeb());

        // Control positivo del recorrido: si la enumeración de ficheros dejara
        // de encontrar la Web, todas las superficies saldrían a 0.
        medido.Should().ContainKey("Components/Legal",
            "el instrumento tiene que ver al menos los textos legales, que son markup puro");

        var discrepancias = medido.Keys.Union(Congelado.Keys)
            .OrderBy(s => s, StringComparer.Ordinal)
            .Select(s => (superficie: s,
                          actual: medido.TryGetValue(s, out var m) ? m.Total : 0,
                          congelado: Congelado.GetValueOrDefault(s)))
            .Where(d => d.actual != d.congelado)
            .ToList();

        var detalle = string.Join("\n", discrepancias.Select(d =>
            $"  {d.superficie}: medido {d.actual}, congelado {d.congelado}" +
            (d.actual > d.congelado && medido.TryGetValue(d.superficie, out var m)
                ? "\n" + string.Join("\n", m.Textos().Take(40).Select(t => "      «" + t + "»"))
                : "")));

        discrepancias.Should().BeEmpty(
            "cada superficie de Web tiene congelado su número de textos sin localizar. Si sube, " +
            "el texto nuevo va a un .resx (con su .ca-ES.resx) y se pinta con IStringLocalizer; " +
            "si baja porque has migrado, baja la cifra en 'Congelado' en este mismo commit. " +
            "Discrepancias (con los textos medidos cuando sube):\n" + detalle);
    }

    // Controles sintéticos: demuestran que cada familia de patrones casa lo
    // que dice casar. Separados de 'Congelado' a propósito: una lista que hace
    // a la vez de control positivo y de lista de techos se vuelve ciega.

    [Theory]
    [InlineData("<Boton Etiqueta=\"Guardar cambios\" />", "Guardar cambios")]
    [InlineData("<input placeholder=\"Buscar por nombre\" />", "Buscar por nombre")]
    [InlineData("<button aria-label=\"Cerrar panel\"></button>", "Cerrar panel")]
    [InlineData("<p>Sin resultados</p>", "Sin resultados")]
    [InlineData("<p>Hay @total documentos</p>", "Hay documentos")]
    [InlineData("<span>@(x ? \"Vigente\" : \"Caducado\")</span>", "Vigente")]
    public void El_detector_ve_texto_de_interfaz_en_markup(string razor, string esperado)
    {
        DetectorTextosSinLocalizar.MedirRazor(razor).Textos().Should().Contain(esperado);
    }

    [Theory]
    [InlineData("ToastService.Mostrar(\"No pudimos guardar.\");", "No pudimos guardar.")]
    [InlineData("var t = $\"Documento caducado: {n}\";", "Documento caducado: {n}")]
    [InlineData("Estado.Vigente => \"Vigente\",", "Vigente")]
    [InlineData("return \"Revisión pendiente\";", "Revisión pendiente")]
    public void El_detector_ve_texto_de_interfaz_en_CSharp(string codigo, string esperado)
    {
        DetectorTextosSinLocalizar.MedirCs(codigo).Textos().Should().Contain(esperado);
    }

    [Theory]
    [InlineData("_logger.LogWarning(\"No se pudo guardar la cuenta\");")]
    [InlineData("throw new InvalidOperationException(\"No hay cuenta activa\");")]
    [InlineData("var css = \"btn btn-primary\";")]
    [InlineData("var ruta = \"/documentos/importar\";")]
    // Un comentario de línea completa no es interfaz.
    [InlineData("// Sin resultados todavía\nvar x = 1;")]
    public void El_detector_no_cuenta_lo_que_no_es_interfaz_en_CSharp(string codigo)
    {
        DetectorTextosSinLocalizar.MedirCs(codigo).Total.Should().Be(0);
    }

    [Theory]
    [InlineData("<div class=\"panel-lateral\"></div>")]
    [InlineData("@* Sin resultados todavía *@")]
    [InlineData("<style>.a { content: \"Sin datos\"; }</style>")]
    // Lo ya migrado no cuenta: el indexador del localizador es una expresión.
    [InlineData("<button>@Textos[\"IdiomaCambiar\"]</button>")]
    [InlineData("<p>@T[\"Hay\"] @Model.Total</p>")]
    [InlineData("@inject IStringLocalizer<TextosComunes> Textos\n<p>@Textos[\"Volver\"]</p>")]
    [InlineData("@if (x)\n{\n<br />\n}\nelse\n{\n<br />\n}")]
    public void El_detector_no_cuenta_lo_que_no_es_interfaz_en_markup(string razor)
    {
        DetectorTextosSinLocalizar.MedirRazor(razor).Total.Should().Be(0);
    }

    private static string RaizWeb()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual is null
            ? throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio.")
            : Path.Combine(actual.FullName, "src", "CaeManager.Web");
    }
}

/// <summary>Cadenas distintas de una superficie, separadas por familia (la suma es el recuento del trinquete).</summary>
internal sealed class TextosDeSuperficie
{
    public HashSet<string> Atributos { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Markup { get; } = new(StringComparer.Ordinal);
    public HashSet<string> CSharp { get; } = new(StringComparer.Ordinal);

    public int Total => Atributos.Count + Markup.Count + CSharp.Count;

    public IEnumerable<string> Textos() =>
        Atributos.Concat(Markup).Concat(CSharp).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal);
}

/// <summary>
/// Detector léxico de texto de interfaz sin localizar. Regex en un solo sitio;
/// el prototipo que midió su precisión y cobertura vive con el inventario del
/// incremento, fuera de este repositorio.
/// </summary>
internal static class DetectorTextosSinLocalizar
{
    private const string Letra = "A-Za-zÁÉÍÓÚÜÑáéíóúüñ";
    private const string Vacia = "(?:a|tu|te|le|mi|de|del|la|las|el|los|en|no|se|un|una|por|con|para|al|que|sin|ya|hay|su|sus|es|lo|o|y)";

    private static readonly Regex AtributoRazor = new(
        @"(?<![\w\-:@])(?:Etiqueta|EtiquetaGrupo|EtiquetaNavegacion|Titulo|Subtitulo|Descripcion|Mensaje|" +
        @"MensajeError|Kicker|Texto|TextoVacio|TextoTodos|TextoCrear|TextoAccionFinal|TextoBotonConfirmar|" +
        @"Marcador|Explicacion|FormatosTexto|Leyenda|Pista|Seccion|ErrorAdjuntos|ErrorConfirmar|Label|Title|" +
        @"Placeholder|Text|Tooltip|HelperText|aria-label|aria-description|title|placeholder|alt|label)" +
        @"\s*=\s*""(?<v>[^""@]*[" + Letra + @"]{2}[^""@]*)""",
        RegexOptions.Compiled);

    private static readonly Regex TextoRazor = new(
        @">(?<t>[^<>{}]*[" + Letra + @"]{2}[^<>{}]*)<", RegexOptions.Compiled);

    /// <summary>
    /// Expresiones Razor dentro de una línea de texto: se borran antes de exigir
    /// letras. Incluye los indexadores (<c>@Textos["Clave"]</c>): sin ellos, el
    /// texto ya migrado seguiría contando como sin localizar.
    /// </summary>
    private static readonly Regex ExpresionRazor = new(
        @"@\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)|@[A-Za-z_][\w.]*(?:\([^()]*\)|\[[^\[\]]*\])*", RegexOptions.Compiled);

    /// <summary>Líneas de "texto" que son C# residual de cuerpos <c>@if</c>/<c>@switch</c>.</summary>
    private static readonly Regex TextoDescartado = new(
        @"^\s*(?:case\s[^:]*:|default:|break;|else\b.*|\}|\{)\s*$", RegexOptions.Compiled);

    private static readonly Regex LiteralCs = new(
        @"(?<![\w""])\$?@?""(?=(?:[^""\\\r\n]|\\.)*[" + Letra + @"]{2})(?<t>(?:[^""\\\r\n]|\\.)*?" +
        @"(?:[áéíóúñÁÉÍÓÚÑ¿¡]|\b" + Vacia + @"\s[" + Letra + @"]|\b[A-ZÁÉÍÓÚÑ][a-záéíóúñ]+\s[" + Letra + @"]{2,})" +
        @"(?:[^""\\\r\n]|\\.)*)""",
        RegexOptions.Compiled);

    private static readonly Regex EtiquetaCs = new(
        @"(?:=>|\?|:|return)\s*\$?""(?<t>[A-ZÁÉÍÓÚÑ][a-záéíóúñ]{2,})""", RegexOptions.Compiled);

    /// <summary>C# embebido en markup: el literal cuenta solo si lo precede algo que abre una expresión.</summary>
    private static readonly Regex PrefijoCsEnMarkup = new(@"(?:[(,?:]|=>|=\s|return\s)\s*$", RegexOptions.Compiled);

    private static readonly Regex SentenciaExcluida = new(
        @"\bthrow\s+new\s+\w*Exception\b|Console\.\w+\(|" +
        @"\bLog(?:Information|Warning|Error|Debug|Critical|Trace)\s*\(|\b_?logger\.|" +
        @"\[(?:GeneratedRegex|Route|Http\w+|SupplyParameterFrom\w+)\b|Regex\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex DosLetras = new("[" + Letra + "]{2}", RegexOptions.Compiled);
    private static readonly Regex Espacios = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ComentarioRazor = new(@"@\*.*?\*@", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex ComentarioHtml = new(@"<!--.*?-->", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex EstiloOScript = new(@"<(style|script)\b.*?</\1>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    /// <summary>
    /// Directivas Razor de una línea. Un genérico (<c>@inject IStringLocalizer&lt;TextosComunes&gt; Textos</c>)
    /// parece una etiqueta HTML, y lo que la sigue se leería como texto de interfaz.
    /// </summary>
    private static readonly Regex DirectivaRazor = new(
        @"^[^\S\n]*@(?:inject|using|implements|inherits|attribute|typeparam|page|layout|rendermode|namespace|preservewhitespace)\b.*$",
        RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ComentarioLineaCs = new(@"^\s*//.*$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static readonly Regex ComentarioBloqueCs = new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    public static Dictionary<string, TextosDeSuperficie> MedirWeb(string raizWeb)
    {
        var porSuperficie = new Dictionary<string, TextosDeSuperficie>(StringComparer.Ordinal);

        foreach (var archivo in Directory.EnumerateFiles(raizWeb, "*.*", SearchOption.AllDirectories)
                     .Where(a => a.EndsWith(".razor", StringComparison.Ordinal) || a.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(a => !Path.GetRelativePath(raizWeb, a)
                         .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                         .Any(parte => parte is "bin" or "obj")))
        {
            var superficie = Superficie(Path.GetRelativePath(raizWeb, archivo).Replace('\\', '/'));
            if (!porSuperficie.TryGetValue(superficie, out var textos))
                porSuperficie[superficie] = textos = new TextosDeSuperficie();

            var contenido = File.ReadAllText(archivo);
            if (archivo.EndsWith(".razor", StringComparison.Ordinal))
                MedirRazor(contenido, textos);
            else
                MedirCs(contenido, textos);
        }

        return porSuperficie.Where(p => p.Value.Total > 0).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    public static TextosDeSuperficie MedirRazor(string contenido) => MedirRazor(contenido, new TextosDeSuperficie());

    public static TextosDeSuperficie MedirCs(string contenido) => MedirCs(contenido, new TextosDeSuperficie());

    /// <summary>
    /// <c>Features/X/…</c> → <c>X</c>; <c>Components/X/…</c> → <c>Components/X</c>;
    /// <c>Components/…</c> suelto → <c>Components(raiz)</c>; otra carpeta → <c>Web/X</c>;
    /// fichero en la raíz → <c>Web(raiz)</c>.
    /// </summary>
    private static string Superficie(string relativa)
    {
        var partes = relativa.Split('/');
        return partes switch
        {
            ["Features", var feature, _, ..] => feature,
            ["Components", var carpeta, _, ..] => "Components/" + carpeta,
            ["Components", _] => "Components(raiz)",
            [var carpeta, _, ..] => "Web/" + carpeta,
            _ => "Web(raiz)",
        };
    }

    private static TextosDeSuperficie MedirRazor(string contenido, TextosDeSuperficie textos)
    {
        var limpio = Normalizar(contenido);
        limpio = ComentarioRazor.Replace(limpio, "");
        limpio = ComentarioHtml.Replace(limpio, "");
        limpio = EstiloOScript.Replace(limpio, "");
        limpio = DirectivaRazor.Replace(limpio, "");

        var inicioCodigo = limpio.IndexOf("@code", StringComparison.Ordinal);
        var markup = inicioCodigo >= 0 ? limpio[..inicioCodigo] : limpio;
        var codigo = inicioCodigo >= 0 ? limpio[inicioCodigo..] : "";

        foreach (Match m in AtributoRazor.Matches(markup))
            textos.Atributos.Add(m.Groups["v"].Value.Trim());

        foreach (Match m in TextoRazor.Matches(markup))
        {
            foreach (var linea in m.Groups["t"].Value.Split('\n'))
            {
                var texto = Espacios.Replace(ExpresionRazor.Replace(linea, " "), " ").Trim();
                if (texto.Length > 0 && DosLetras.IsMatch(texto) && !TextoDescartado.IsMatch(texto))
                    textos.Markup.Add(texto);
            }
        }

        foreach (var sentencia in markup.Split(';'))
        {
            if (SentenciaExcluida.IsMatch(sentencia)) continue;
            foreach (Match m in LiteralCs.Matches(sentencia))
            {
                var inicio = Math.Max(0, m.Index - 12);
                if (PrefijoCsEnMarkup.IsMatch(sentencia[inicio..m.Index]))
                    textos.CSharp.Add(m.Groups["t"].Value);
            }
            foreach (Match m in EtiquetaCs.Matches(sentencia))
                textos.CSharp.Add(m.Groups["t"].Value);
        }

        return MedirCs(codigo, textos, yaNormalizado: true);
    }

    private static TextosDeSuperficie MedirCs(string codigo, TextosDeSuperficie textos, bool yaNormalizado = false)
    {
        var limpio = yaNormalizado ? codigo : Normalizar(codigo);
        limpio = ComentarioLineaCs.Replace(limpio, "");
        limpio = ComentarioBloqueCs.Replace(limpio, "");

        foreach (var sentencia in limpio.Split(';'))
        {
            if (SentenciaExcluida.IsMatch(sentencia)) continue;
            foreach (Match m in LiteralCs.Matches(sentencia))
                textos.CSharp.Add(m.Groups["t"].Value);
            foreach (Match m in EtiquetaCs.Matches(sentencia))
                textos.CSharp.Add(m.Groups["t"].Value);
        }

        return textos;
    }

    /// <summary>
    /// Finales de línea a <c>\n</c>: con <c>core.autocrlf=true</c> el árbol de
    /// Windows tiene CRLF y el de CI LF, y el recuento no puede depender de eso
    /// (el <c>$</c> multilínea de .NET no se detiene antes de un <c>\r</c>).
    /// </summary>
    private static string Normalizar(string contenido) => contenido.Replace("\r\n", "\n");
}
