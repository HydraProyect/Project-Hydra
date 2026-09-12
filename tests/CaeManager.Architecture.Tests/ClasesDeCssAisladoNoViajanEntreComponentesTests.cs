using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Una clase declarada en un <c>.razor.css</c> solo puede usarse en <b>su
/// propio</b> componente. El CSS aislado de Blazor compila cada selector con
/// el atributo de ámbito de su componente —<c>.descripcion-delegaciones</c>
/// pasa a <c>.descripcion-delegaciones[b-41fh1yr74k]</c>—, así que copiar el
/// nombre de la clase a otra pantalla produce markup <b>sin estilo alguno</b>.
///
/// <para>
/// <b>No falla, no avisa y no se ve en revisión.</b> El elemento se pinta, el
/// texto se lee, y solo pierde color, ancho máximo y margen. Medido el
/// 2026-09-09 sobre <c>origin/main</c>: ONCE párrafos estaban así en nueve
/// componentes de ocho features. Ocho de esos componentes ni siquiera tienen
/// <c>.razor.css</c> propio, de modo que sus elementos no llevan atributo de
/// ámbito y ningún selector aislado podía casar jamás.
/// </para>
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Esto comprueba
/// nombres de clase en atributos <c>class="…"</c> literales del fuente. NO
/// comprueba el estilo calculado, ni clases compuestas en C# o interpoladas
/// con <c>@</c> —esas se saltan a propósito, porque el fragmento literal no
/// dice qué clase acaba en el DOM—, ni detecta la situación inversa (una
/// clase declarada y no usada por nadie).
/// </para>
///
/// <para>
/// <b><c>::deep</c> queda fuera y tiene que quedar fuera.</b> Es justamente el
/// mecanismo con el que un componente SÍ estiliza el markup de sus hijos
/// (<c>EstadoVacio.razor.css</c> hace <c>.estado-vacio-icono ::deep .icono</c>
/// para dimensionar el svg que pinta <c>Icono.razor</c>). Contar esos
/// selectores como «declaración de la clase» convertiría cada uso legítimo en
/// una infracción.
/// </para>
/// </summary>
public class ClasesDeCssAisladoNoViajanEntreComponentesTests
{
    /// <summary>
    /// Deuda congelada: usos que hoy incumplen y que este incremento NO
    /// arregla. El trinquete no los repara — impide que la lista crezca.
    ///
    /// <para>
    /// <b>Son 31, y ninguno es de descripción de página.</b> Los once párrafos
    /// de descripción que motivaron este trinquete se cerraron con
    /// <c>CabeceraPagina</c> y <c>.texto-descriptivo</c>: por eso no hay aquí
    /// ni una entrada <c>descripcion-*</c>. Lo que queda son otras cinco
    /// familias del mismo defecto, encontradas al medir por primera vez con un
    /// instrumento que las podía ver:
    /// <list type="bullet">
    /// <item><b>botón a pelo</b> (8) — <c>.boton</c> y sus variantes se
    /// escriben sobre un <c>&lt;button&gt;</c> en vez de usar
    /// <c>&lt;Boton&gt;</c>. Se cierran usando el componente.</item>
    /// <item><b><c>workspace-lista-entidades</c></b> (9) — la lista de
    /// entidades relacionadas de los siete workspaces, declarada en
    /// <c>FilaEntidadRelacionada.razor.css</c>. Su sitio es
    /// <c>wwwroot/css/workspace.css</c>.</item>
    /// <item><b>avisos</b> (4) — <c>aviso-error</c> y <c>aviso-revocacion</c>
    /// copiados entre features; les corresponde una primitiva de aviso.</item>
    /// <item><b>controles de formulario a pelo</b> (2) —
    /// <c>campo-input</c>, <c>campo-info-vacio</c>. Los dos de Tipos de
    /// documento se cerraron con estilo propio en su <c>.razor.css</c>.</item>
    /// <item><b>tres pistas y tres barras de confianza</b> (6) — copiadas de
    /// <c>Trabajadores.razor</c> y de <c>RevisionIa.razor</c>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Cada vez que una se corrige, se borra de aquí. Un uso nuevo no puede
    /// entrar sin que alguien lo escriba a mano, que es exactamente el punto
    /// en que se piensa lo que se está haciendo. Clave:
    /// <c>Componente.razor::clase</c>, y el motivo es obligatorio.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> DeudaCongelada = new()
    {
        ["ClavesApi.razor::aviso-error"] = "aviso de error copiado de Facturacion/Proyectos; le corresponde una primitiva de aviso",
        ["ConectarExtension.razor::aviso-error"] = "aviso de error copiado de Facturacion/Proyectos; le corresponde una primitiva de aviso",
        ["ConectoresCae.razor::aviso-error"] = "aviso de error copiado de Facturacion/Proyectos; le corresponde una primitiva de aviso",
        ["ClavesApi.razor::aviso-revocacion"] = "aviso copiado de Delegaciones; la misma primitiva de aviso, pendiente",
        ["CentroWorkspacePanel.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["ClienteWorkspacePanel.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["Conexiones.razor::boton"] = "markup de boton a pelo en vez del componente Boton",
        ["Conexiones.razor::boton-medio"] = "markup de boton a pelo en vez del componente Boton",
        ["Conexiones.razor::boton-primario"] = "markup de boton a pelo en vez del componente Boton",
        ["Delegaciones.razor::aviso-error"] = "aviso de error copiado de Facturacion/Proyectos; le corresponde una primitiva de aviso",
        ["EmpresaWorkspacePanel.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["Empresas.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["PestanaBlindaje42.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["PestanaDocumentacion.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["Plataforma.razor::boton"] = "markup de boton a pelo en vez del componente Boton",
        ["Plataforma.razor::boton-primario"] = "markup de boton a pelo en vez del componente Boton",
        ["Proyectos.razor::enlace-accion"] = "enlace de accion copiado de Facturacion",
        ["Retencion.razor::aviso-error"] = "aviso de error copiado de Facturacion/Proyectos; le corresponde una primitiva de aviso",
        ["RevisionIaTab.razor::revision-ia-confianza"] = "barra de confianza que la pestana comparte con RevisionIa.razor",
        ["RevisionIaTab.razor::revision-ia-confianza-barra"] = "barra de confianza que la pestana comparte con RevisionIa.razor",
        ["RevisionIaTab.razor::revision-ia-confianza-texto"] = "barra de confianza que la pestana comparte con RevisionIa.razor",
        ["SelectorEntidad.razor::campo-input"] = "control de formulario a pelo en vez de CampoTexto o CampoBuscarSelect",
        ["SubcontrataWorkspacePanel.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["SugerenciasPreventivasTab.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["TrabajadorWorkspacePanel.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["Usuarios.razor::pista-documento"] = "pista de validacion copiada de Trabajadores; primitiva de pista pendiente",
        ["Usuarios.razor::pista-documento-error"] = "pista de validacion copiada de Trabajadores; primitiva de pista pendiente",
        ["Usuarios.razor::pista-documento-exito"] = "pista de validacion copiada de Trabajadores; primitiva de pista pendiente",
        ["Visitas.razor::workspace-lista-entidades"] = "lista de entidades relacionadas de los siete workspaces; la regla vive en FilaEntidadRelacionada.razor.css y le corresponde a workspace.css",
        ["VisorDocumento.razor::boton"] = "markup de boton a pelo en vez del componente Boton",
        ["VisorDocumento.razor::boton-medio"] = "markup de boton a pelo en vez del componente Boton",
        ["VisorDocumento.razor::boton-secundario"] = "markup de boton a pelo en vez del componente Boton",
    };

    [Fact]
    public void Ninguna_clase_de_css_aislado_se_usa_fuera_de_su_componente()
    {
        var propietarias = ClasesPorComponentePropietario();
        var globales = ClasesGlobales();

        propietarias.Should().NotBeEmpty(
            "sin ninguna clase aislada localizada, este trinquete estaría en verde por no mirar nada");
        globales.Should().NotBeEmpty(
            "sin las clases globales, cada uso de .contenedor-pagina sería una falsa alarma");

        var infracciones = new List<string>();

        foreach (var (ruta, contenido) in ComponentesRazor())
        {
            var propio = Path.GetFileName(ruta);

            foreach (var clase in ClasesUsadas(contenido))
            {
                // Una clase global gana: está en un .css de wwwroot y llega a todos.
                if (globales.Contains(clase)) continue;
                if (!propietarias.TryGetValue(clase, out var duenos)) continue;
                if (duenos.Contains(propio)) continue;

                var clave = $"{propio}::{clase}";
                if (DeudaCongelada.ContainsKey(clave)) continue;

                infracciones.Add(
                    $"{clave}  (declarada solo en {string.Join(", ", duenos.OrderBy(d => d))})");
            }
        }

        string.Join("\n", infracciones.Distinct().OrderBy(x => x)).Should().BeEmpty(
            "una clase de CSS aislado usada en otro componente no aplica ningún estilo: el markup se pinta "
            + "desnudo y nadie lo nota. Mueve la regla a un .css global si de verdad es compartida, saca el "
            + "markup a un componente del sistema de diseño, o añade el uso a DeudaCongelada CON su motivo");
    }

    /// <summary>
    /// Prueba de que el instrumento mira donde dice mirar. Sin esto, un cambio
    /// de rutas lo dejaría en verde por no encontrar ficheros — el modo de
    /// fallo más común de un trinquete de fuente, y el que ya se pagó una vez
    /// en <c>ListasDistinguenVacioPorFiltroTests</c>.
    /// </summary>
    [Fact]
    public void El_escaner_encuentra_componentes_clases_aisladas_y_clases_globales()
    {
        ComponentesRazor().Select(c => Path.GetFileName(c.Ruta))
            .Should().Contain("Delegaciones.razor").And.Contain("CabeceraPagina.razor",
                "el escáner tiene que ver tanto las páginas de Features como el sistema de diseño");

        ClasesPorComponentePropietario().Should().ContainKey("bandeja-chip",
            "es una clase declarada en Bandeja.razor.css, el caso normal que este trinquete vigila");

        ClasesGlobales().Should().Contain("contenedor-pagina").And.Contain("texto-descriptivo",
            "ambas viven en wwwroot/css y se usan desde cualquier componente");
    }

    /// <summary>
    /// Prueba de sensibilidad permanente: el detector reconoce como infracción
    /// el caso exacto que se corrigió. Si alguien relaja <see cref="ClasesUsadas"/>
    /// o la lectura de los <c>.razor.css</c>, esto se pone rojo aunque la
    /// deuda siga vacía.
    /// </summary>
    [Fact]
    public void El_detector_reconoce_una_clase_aislada_usada_desde_fuera()
    {
        var propietarias = ClasesPorComponentePropietario();
        var globales = ClasesGlobales();

        // "bandeja-chip" está declarada en Bandeja.razor.css y en ningún .css global.
        globales.Should().NotContain("bandeja-chip");
        propietarias["bandeja-chip"].Should().Contain("Bandeja.razor");

        var ajeno = ClasesUsadas("""<p class="bandeja-chip">texto</p>""").ToList();

        ajeno.Should().Contain("bandeja-chip");
        propietarias["bandeja-chip"].Should().NotContain("Retencion.razor",
            "si lo hiciera, el uso desde Retencion.razor dejaría de ser infracción y este trinquete "
            + "estaría midiendo otra cosa");
    }

    /// <summary>
    /// Clase → componentes que la declaran en su <c>.razor.css</c>. Se ignoran
    /// los selectores con <c>::deep</c>: ahí la clase es del hijo a propósito.
    /// </summary>
    private static Dictionary<string, HashSet<string>> ClasesPorComponentePropietario()
    {
        var mapa = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var css in FicherosBajo(Path.Combine("src", "CaeManager.Web"), "*.razor.css"))
        {
            var componente = Path.GetFileNameWithoutExtension(css); // "Delegaciones.razor"

            foreach (var selector in BloquesDeSelector(File.ReadAllText(css)))
            {
                if (selector.Contains("::deep", StringComparison.Ordinal)) continue;

                foreach (Match m in Regex.Matches(selector, @"\.(-?[_a-zA-Z][\w-]*)"))
                {
                    var clase = m.Groups[1].Value;
                    if (!mapa.TryGetValue(clase, out var duenos))
                        mapa[clase] = duenos = new HashSet<string>(StringComparer.Ordinal);
                    duenos.Add(componente);
                }
            }
        }

        return mapa;
    }

    /// <summary>Clases declaradas en CSS no aislado: wwwroot llega a todo el documento.</summary>
    private static HashSet<string> ClasesGlobales()
    {
        var clases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var css in FicherosBajo(Path.Combine("src", "CaeManager.Web", "wwwroot"), "*.css"))
        {
            foreach (var selector in BloquesDeSelector(File.ReadAllText(css)))
                foreach (Match m in Regex.Matches(selector, @"\.(-?[_a-zA-Z][\w-]*)"))
                    clases.Add(m.Groups[1].Value);
        }

        return clases;
    }

    /// <summary>
    /// La parte de selector que precede a cada <c>{</c>. Basta para extraer
    /// nombres de clase y evita traerse valores de propiedades (que también
    /// llevan puntos: <c>0.875rem</c>).
    /// </summary>
    private static IEnumerable<string> BloquesDeSelector(string css)
    {
        var sinComentarios = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        foreach (Match m in Regex.Matches(sinComentarios, @"(?:^|[}])([^{}]*)\{", RegexOptions.Multiline))
        {
            var selector = m.Groups[1].Value;
            // Las at-rules (@media, @supports) envuelven bloques; su condición
            // no declara clases, pero lo de dentro sí lo hace y ya lo recoge
            // la propia iteración.
            if (selector.TrimStart().StartsWith('@')) continue;
            yield return selector;
        }
    }

    /// <summary>
    /// Nombres de clase de los atributos <c>class="…"</c> literales. Se
    /// descartan los valores con <c>@</c>: ahí la clase la decide C# en
    /// ejecución y el fuente no dice cuál acaba en el DOM.
    /// </summary>
    private static IEnumerable<string> ClasesUsadas(string razor)
    {
        foreach (Match m in Regex.Matches(razor, @"\bclass=""([^""]*)"""))
        {
            var valor = m.Groups[1].Value;
            if (valor.Contains('@')) continue;

            foreach (var clase in valor.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                yield return clase;
        }
    }

    private static List<(string Ruta, string Contenido)> ComponentesRazor() =>
        FicherosBajo(Path.Combine("src", "CaeManager.Web"), "*.razor")
            .Select(f => (Ruta: f, Contenido: File.ReadAllText(f)))
            .ToList();

    private static List<string> FicherosBajo(string relativa, string patron)
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), relativa);
        if (!Directory.Exists(carpeta)) return [];

        return Directory
            .EnumerateFiles(carpeta, patron, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}
