using System.Text.RegularExpressions;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// CWE-602, «validación solo en el cliente». En Blazor Server el atributo <c>disabled</c> es una pista para el navegador, no una
/// garantía: quien lo quita en el DOM consigue que el clic llegue al manejador del servidor. Por eso el estado deshabilitado se
/// vuelve a comprobar dentro del manejador (<c>DialogoConfirmacion</c> con <c>ConfirmarDeshabilitado</c> era el patrón; <c>Boton</c>,
/// <c>BotonConEspera</c>, <c>DrawerFormulario</c> y <c>ModalFormulario</c> lo copian).
///
/// <para>
/// <b>Contrato efectivo, más estrecho que el nombre.</b> Dos propiedades sobre los <c>.razor</c> del kit (<c>Components/DesignSystem</c>):
/// (1) un componente con un parámetro <c>bool</c> de deshabilitado no enlaza un <c>EventCallback</c> parámetro directamente a
/// <c>@onclick</c> (así el clic llegaría al llamador sin pasar por ninguna comprobación); (2) todo <c>&lt;button&gt;</c>, <c>&lt;input&gt;</c>, <c>&lt;select&gt;</c>, <c>&lt;textarea&gt;</c> o <c>&lt;InputFile&gt;</c> con
/// <c>disabled="@…"</c> cuyo manejador (<c>@onclick</c>, <c>@oninput</c>, <c>@onchange</c>, <c>@onblur</c> u <c>@onfocus</c>, o <c>OnChange</c>; el <c>@onkeydown</c> no, que solo navega) es un método del propio componente tiene un cuerpo que menciona alguno de los
/// identificadores de la expresión de <c>disabled</c>, él o un método del componente al que llama (un nivel). NO ve: manejadores en lambda (<c>@onclick="() =&gt; X()"</c>, que no se
/// pueden inspeccionar por texto), la lógica que el método delega en otro, un identificador que se mencione pero no se use para
/// retornar. Es un trinquete de forma, no una prueba de comportamiento: el comportamiento
/// lo prueban los bUnit de cada componente (quitan <c>disabled</c> y comprueban que el llamador no se entera).
/// </para>
/// </summary>
public class BotonesDelKitReafirmanElDeshabilitadoEnElServidorTests
{
    /// <summary>
    /// Botones del kit que hoy no reafirman su deshabilitado en el servidor. Solo puede bajar: se retira de aquí al arreglarlo.
    /// </summary>
    private static readonly HashSet<string> SinReafirmarTodavia = new(StringComparer.Ordinal)
    {
    };

    private static readonly Regex EtiquetaBoton = new(@"<(?:button|input|select|textarea|InputFile)\b(?:=>|[^>])*>", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AtributoDisabled = new(@"\sdisabled=""@\(?([^""]*?)\)?""", RegexOptions.Compiled);
    private static readonly Regex ManejadorMetodo = new(@"(?:@on(?:click|input|change|blur|focus)|OnChange)=""@?([A-Za-z_]\w*)""", RegexOptions.Compiled);
    private static readonly Regex Identificador = new(@"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);
    private static readonly Regex ParametroBoolDeshabilitado = new(@"\[Parameter[^\]\n]*\][^\n]*\bbool\s+(\w*(?:Deshabilitad|Habilitad)\w*)", RegexOptions.Compiled);
    private static readonly Regex ParametroEventCallback = new(@"\[Parameter[^\]\n]*\][^\n]*\bEventCallback(?:<[^>\n]+>)?\s+(\w+)", RegexOptions.Compiled);

    private static IEnumerable<(string Nombre, string Razor, string Codigo)> Componentes()
    {
        var diseno = Path.Combine(MarcadoRazor.RaizDelRepositorio(), "src", "CaeManager.Web", "Components", "DesignSystem");
        foreach (var ruta in Directory.EnumerateFiles(diseno, "*.razor", SearchOption.AllDirectories).OrderBy(r => r, StringComparer.Ordinal))
        {
            var razor = File.ReadAllText(ruta);
            var cs = ruta + ".cs";
            var codigo = razor + (File.Exists(cs) ? "\n" + File.ReadAllText(cs) : string.Empty);
            yield return (Path.GetFileNameWithoutExtension(ruta), razor, codigo);
        }
    }

    [Fact]
    public void Un_EventCallback_del_kit_con_deshabilitado_no_se_enlaza_directo_a_onclick()
    {
        var infractores = new List<string>();
        foreach (var (nombre, razor, codigo) in Componentes())
        {
            if (!ParametroBoolDeshabilitado.IsMatch(codigo)) continue;

            foreach (Match callback in ParametroEventCallback.Matches(codigo))
            {
                var parametro = callback.Groups[1].Value;
                if (Regex.IsMatch(razor, $@"@onclick=""@?{Regex.Escape(parametro)}"""))
                    infractores.Add($"{nombre}: @onclick=\"{parametro}\"");
            }
        }

        infractores.Should().BeEmpty(
            "un EventCallback enlazado directo a @onclick deja pasar el clic de un botón que el navegador reactivó (CWE-602); " +
            "se enlaza a un método que reafirma el estado deshabilitado, como Boton.AlPulsarAsync");
    }

    [Fact]
    public void Los_manejadores_de_clic_de_los_botones_con_disabled_del_kit_mencionan_su_condicion()
    {
        var infractores = new List<string>();
        var vistos = 0;
        foreach (var (nombre, razor, codigo) in Componentes())
        {
            foreach (Match etiqueta in EtiquetaBoton.Matches(razor))
            {
                var disabled = AtributoDisabled.Match(etiqueta.Value);
                if (!disabled.Success) continue;

                var condicion = Identificador.Matches(disabled.Groups[1].Value).Select(m => m.Value)
                    .Where(i => i is not ("true" or "false" or "null" or "is" or "not" or "and" or "or")).ToList();
                foreach (var metodo in ManejadorMetodo.Matches(etiqueta.Value).Select(m => m.Groups[1].Value).Distinct())
                {
                    vistos++;
                    var cuerpo = CuerpoDelManejador(codigo, metodo);
                    var clave = $"{nombre}.{metodo}";
                    // Un manejador que no es un método del componente (un EventCallback propio) no se puede inspeccionar aquí.
                    if (cuerpo is null) continue;

                    var menciona = condicion.Any(i => Regex.IsMatch(cuerpo, $@"\b{Regex.Escape(i)}\b"));
                    if (!menciona && !SinReafirmarTodavia.Contains(clave))
                        infractores.Add($"{clave} (disabled depende de: {string.Join(", ", condicion)})");
                }
            }
        }

        vistos.Should().BeGreaterThan(0, "el trinquete tiene que ver botones con disabled y @onclick de método; si no ve ninguno, dejó de medir");
        infractores.Should().BeEmpty(
            "el manejador de un botón con disabled vuelve a comprobar esa condición: quitar `disabled` en el DOM no puede ejecutar la acción (CWE-602)");
    }

    /// <summary>
    /// Lo que ejecuta el manejador: el cuerpo del método, o, si es una propiedad que elige el manejador (<c>OnClick.HasDelegate ? AlPulsarAsync : null</c>,
    /// que registra el clic solo si hay a quién avisar), el cuerpo de los métodos que esa propiedad nombra. Nulo si no es ninguna de las dos cosas.
    /// </summary>
    private static string? CuerpoDelManejador(string codigo, string nombre)
    {
        var directo = CuerpoDelMetodo(codigo, nombre);
        if (directo is not null)
        {
            // Un nivel de delegación: «Alternar» abre llamando a «Abrir», que es quien comprueba. Los métodos llamados cuentan como parte del manejador.
            var llamados = Identificador.Matches(directo).Select(m => m.Value).Distinct().Where(i => i != nombre)
                .Select(i => CuerpoDelMetodo(codigo, i)).Where(c => c is not null);
            return directo + "\n" + string.Join("\n", llamados);
        }

        var propiedad = Regex.Match(codigo, $@"\b\w+\??\s+{Regex.Escape(nombre)}\s*=>\s*([^;]*);");
        if (!propiedad.Success) return null;

        var cuerpos = Identificador.Matches(propiedad.Groups[1].Value).Select(m => CuerpoDelMetodo(codigo, m.Value)).Where(c => c is not null);
        return string.Join("\n", cuerpos);
    }

    /// <summary>Cuerpo (entre llaves, o la expresión de un <c>=&gt;</c>) del método con ese nombre, o nulo si el componente no lo declara.</summary>
    private static string? CuerpoDelMetodo(string codigo, string metodo)
    {
        var cabecera = Regex.Match(codigo, $@"\b(?:Task|ValueTask|void)(?:<[^>\n]+>)?\s+{Regex.Escape(metodo)}\s*\([^)]*\)\s*(=>|\{{)");
        if (!cabecera.Success) return null;

        var inicio = cabecera.Index + cabecera.Length;
        if (cabecera.Groups[1].Value == "=>")
        {
            var fin = codigo.IndexOf(';', inicio);
            return fin < 0 ? codigo[inicio..] : codigo[inicio..fin];
        }

        var profundidad = 1;
        for (var i = inicio; i < codigo.Length; i++)
        {
            if (codigo[i] == '{') profundidad++;
            else if (codigo[i] == '}' && --profundidad == 0) return codigo[inicio..i];
        }

        return codigo[inicio..];
    }
}
