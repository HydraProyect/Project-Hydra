using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión 1:1 del Cuadro de dirección y de los Indicadores de IA y BPO frente a sus maquetas (2026-11-08). bUnit no
/// calcula estilos, así que se congelan las reglas de las hojas que corrigen cada divergencia medida, con control
/// positivo.
/// </summary>
public class RevisionUnoAUnoDireccionHojasDeEstiloTests
{
    /// <summary>
    /// A 375 px los tres momentos del periodo se apilan en columna (maqueta: .tv-seg de tres filas). En fila, «Año 3»
    /// desbordaba una caja de 266 px y quedaba debajo de la flecha «›».
    /// </summary>
    [Fact]
    public void A_375_px_los_momentos_del_periodo_se_apilan_en_columna()
    {
        var movil = BloqueMedia(Leer("Features", "CuadroDireccion", "Pages", "CuadroDireccion.razor.css"), "max-width: 640px", ".cuadro-momento");

        var momento = Cuerpo(movil, ".cuadro-momento");
        momento.Should().MatchRegex(@"(?<![\w-])flex-direction\s*:\s*column\s*;");
        momento.Should().MatchRegex(@"(?<![\w-])height\s*:\s*auto\s*;");
        var periodo = Cuerpo(movil, ".cuadro-periodo");
        periodo.Should().MatchRegex(@"(?<![\w-])height\s*:\s*auto\s*;");
        periodo.Should().MatchRegex(@"(?<![\w-])justify-content\s*:\s*center\s*;", "las flechas quedan a los lados del grupo centrado");
        Cuerpo(movil, ".cuadro-momento button").Should().MatchRegex(@"line-height\s*:\s*22px", "botones de 34 px como la maqueta");
        movil.Should().NotMatchRegex(@"\.cuadro-momento button\s*\{[^}]*padding\s*:\s*0 8px",
            "el relleno 0 8px era el parche de la fila que no cabía; en columna los botones conservan 6 12 de la maqueta");
    }

    /// <summary>El título de Indicadores es de 26/700/32, no el 36 px del .titulo-pagina global.</summary>
    [Fact]
    public void El_titulo_de_indicadores_es_de_26_px()
    {
        var cuerpo = Cuerpo(LeerIndicadores(), ".contenedor-pagina ::deep .titulo-pagina");

        cuerpo.Should().MatchRegex(@"font\s*:\s*700\s+26px/32px");
    }

    /// <summary>
    /// Las tarjetas de KPI se separan 32 px. La raíz de Tarjeta es de otro componente: la regla necesita ::deep o no
    /// le llega (sin él, iban pegadas).
    /// </summary>
    [Fact]
    public void Las_tarjetas_de_kpi_se_separan_32_px_con_deep()
    {
        var css = LeerIndicadores();

        Cuerpo(css, ".contenedor-pagina ::deep .tarjeta-kpi").Should().Contain("margin-bottom: var(--space-8)");
        css.Should().NotMatchRegex(@"(?m)^\.tarjeta-kpi\s*\{", "una regla sin ::deep no alcanza la raíz de Tarjeta");
    }

    private static string LeerIndicadores() => Leer("Features", "DashboardEjecutivo", "Pages", "DashboardEjecutivo.razor.css");

    /// <summary>El bloque @media con esa condición que contiene <paramref name="contiene"/> (la hoja tiene varios).</summary>
    private static string BloqueMedia(string css, string condicion, string contiene)
    {
        var cabecera = "@media (" + condicion + ")";
        for (var inicio = css.IndexOf(cabecera, StringComparison.Ordinal); inicio >= 0;
             inicio = css.IndexOf(cabecera, inicio + 1, StringComparison.Ordinal))
        {
            var abre = css.IndexOf('{', inicio);
            var profundidad = 0;
            for (var i = abre; i < css.Length; i++)
            {
                if (css[i] == '{') profundidad++;
                else if (css[i] == '}' && --profundidad == 0)
                {
                    var bloque = css[(abre + 1)..i];
                    if (bloque.Contains(contiene, StringComparison.Ordinal)) return bloque;
                    break;
                }
            }
        }
        throw new InvalidOperationException($"control positivo: ningún {cabecera} contiene {contiene}");
    }

    private static string Cuerpo(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^\s*" + Regex.Escape(selector) + @"\s*\{(?<cuerpo>[^}]*)\}");
        m.Success.Should().BeTrue($"control positivo: existe la regla {selector}");
        return m.Groups["cuerpo"].Value;
    }

    private static string Leer(params string[] partes) => File.ReadAllText(Ruta(partes));

    private static string Ruta(params string[] partes)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer las hojas de estilo");
        return Path.Combine([dir!, "src", "CaeManager.Web", .. partes]);
    }
}
