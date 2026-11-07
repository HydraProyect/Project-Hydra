using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maquetación que bUnit no calcula (el tbody de la lista de Visitas es un bloque y sus filas
/// de dato, una rejilla con huecos): se inspecciona la hoja de estilos fuente, como
/// <c>PulidoUxLoteFTests</c>. El ancho real y el fondo en hover se miden en el navegador.
/// </summary>
public class VisitasListaFilaCompletaCssTests
{
    private static string Css()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Visitas", "Pages", "Visitas.razor.css"));
    }

    /// <summary>
    /// Cuerpos de las reglas cuya lista de selectores contiene <paramref name="selector"/> como un selector entero
    /// (no importa el orden de la lista ni la sangría).
    /// </summary>
    private static List<string> Reglas(string selector)
    {
        var css = Regex.Replace(Css(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var cuerpos = new List<string>();
        foreach (Match m in Regex.Matches(css, @"(?<sel>[^{}]+)\{(?<c>[^{}]*)\}"))
        {
            var lista = m.Groups["sel"].Value.Split(',').Select(x => Regex.Replace(x, @"\s+", " ").Trim());
            if (lista.Contains(selector)) cuerpos.Add(m.Groups["c"].Value);
        }
        cuerpos.Should().NotBeEmpty($"debe existir una regla con el selector «{selector}»");
        return cuerpos;
    }

    private static void Declara(string selector, string propiedadYValor, string porque) =>
        Reglas(selector).Should().Contain(c => Regex.IsMatch(c, @"(^|;|\s)" + propiedadYValor), porque);

    [Fact]
    public void La_fila_desplegada_es_un_bloque_de_ancho_completo_y_lleva_el_fondo()
    {
        const string fila = ".visitas-lista tr.visitas-fila-desplegada";
        Declara(fila, @"display\s*:\s*block", "un table-row en un tbody en bloque se ajusta al contenido (≈ 2/3 del ancho)");
        Declara(fila, @"width\s*:\s*100%", "ocupa el ancho de la fila de dato");
        Declara(fila, @"background-color\s*:", "el fondo es de la fila, continuo hasta el borde derecho");
    }

    [Fact]
    public void Las_celdas_de_la_fila_desplegada_no_repintan_el_fondo_ni_con_el_puntero()
    {
        Declara(".visitas-lista .visitas-fila-desplegada > td", @"background-color\s*:\s*transparent", "el fondo está en el tr");
        Declara(".visitas-lista .visitas-fila-desplegada:hover > td", @"background-color\s*:\s*transparent",
            "list-page.css pinta .tabla-datos tr:hover td: sin esto el td se vería gris sobre el fondo del tr");
    }

    [Fact]
    public void La_fila_de_grupo_es_un_bloque_de_ancho_completo_y_no_la_oculta_la_regla_de_filas_vacias()
    {
        const string grupo = ".tabla-datos.visitas-lista tbody tr.visitas-grupo";
        Declara(grupo, @"display\s*:\s*block", "list-page.css oculta (display:none) las filas sin td con contenido, y la de grupo solo tiene th");
        Declara(grupo, @"width\s*:\s*100%", "ocupa el ancho de la fila de dato");
    }

    [Fact]
    public void El_hover_y_el_foco_los_pinta_la_fila_y_no_cada_celda()
    {
        Declara(".visitas-lista tr.fila-pulsable:hover", @"background-color\s*:\s*var\(--color-fila-hover\)", "el token de hover de la tabla");
        Declara(".visitas-lista tr.fila-pulsable.fila-enfocada", @"background-color\s*:", "el foco de j/k lo pinta la fila");
        Declara(".visitas-lista tr.fila-pulsable:hover td", @"background-color\s*:\s*transparent",
            "las celdas no pintan el hover de .tabla-datos: con el gap de la rejilla dejaría franjas sin fondo");
        Declara(".visitas-lista tr.fila-pulsable.fila-enfocada td", @"box-shadow\s*:\s*none",
            "la barra de foco de j/k la lleva la fila: por celda se repetiría una barra en cada columna");
    }
}
