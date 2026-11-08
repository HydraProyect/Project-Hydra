using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maquetación que bUnit no calcula (la alineación del texto de un botón): se inspecciona la hoja de estilos
/// fuente, como <c>VisitasListaFilaCompletaCssTests</c>. La alineación real se midió en el navegador contra
/// «Gestion Pasos TALVEG.dc.html», donde los valores del documento van a la izquierda, bajo su clave.
/// </summary>
public class GestionAutomaticaDetalleHojaDeEstilosTests
{
    private static string Css()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Gestiones", "Pages", "GestionAutomaticaDetalle.razor.css"));
    }

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

    [Fact]
    public void Los_valores_copiables_del_documento_se_alinean_a_la_izquierda_bajo_su_clave()
    {
        const string selector = ".gp-campo ::deep .boton-copiar-en-linea";
        var reglas = Reglas(selector);
        reglas.Should().Contain(c => Regex.IsMatch(c, @"(^|;|\s)text-align\s*:\s*left"),
            "BotonCopiar es un <button> a ancho de celda y un botón centra su texto por defecto");
        reglas.Should().Contain(c => Regex.IsMatch(c, @"(^|;|\s)align-self\s*:\s*flex-start"),
            "en la maqueta el valor copiable mide lo que su texto: el hueco de al lado no copia y la confirmación sale junto al valor");
    }

    [Fact]
    public void La_regla_de_los_valores_copiables_no_esta_dentro_de_una_media_query()
    {
        var css = Regex.Replace(Css(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var primeraMedia = css.IndexOf("@media", StringComparison.Ordinal);
        var regla = css.IndexOf(".gp-campo ::deep .boton-copiar-en-linea", StringComparison.Ordinal);
        regla.Should().BeGreaterThanOrEqualTo(0);
        (primeraMedia < 0 || regla < primeraMedia).Should().BeTrue("la alineación vale a todos los anchos, no solo en pantallas estrechas");
    }
}
