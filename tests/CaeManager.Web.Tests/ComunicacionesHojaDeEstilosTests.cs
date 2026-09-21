using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maquetación de la bandeja de Comunicaciones en staging: los conmutadores de
/// ámbito se desbordaban, el recuento de ruido se partía en vertical, el nombre de
/// un documento citado quedaba en una letra y el botón nativo de archivos se veía
/// junto a «Adjuntar». bUnit no calcula CSS, así que se inspeccionan las hojas.
/// </summary>
public class ComunicacionesHojaDeEstilosTests
{
    private const string Bandeja = "Features/Comunicaciones/Pages/Bandeja.razor.css";
    private const string Composer = "Features/Comunicaciones/Components/ComposerBar.razor.css";

    [Fact]
    public void El_conmutador_de_ambito_no_tiene_altura_fija_y_se_reparte_el_ancho()
    {
        var css = Leer(Bandeja);
        // Los chips de filtro comparten .bandeja-toggle: no deben repartirse el ancho.
        Regla(css, @"\.bandeja-toggle").Should().NotMatchRegex(@"flex\s*:\s*1");
        var regla = Regla(css, @"\.bandeja-vista-toggle \.bandeja-toggle");
        regla.Should().MatchRegex(@"height\s*:\s*auto", "su rótulo ocupa dos líneas en la columna estrecha");
        regla.Should().MatchRegex(@"min-height\s*:");
        regla.Should().MatchRegex(@"flex\s*:\s*1");
    }

    [Fact]
    public void Los_badges_de_la_linea_de_estado_de_la_fila_no_se_parten()
    {
        var regla = Regla(Leer(Bandeja), @"\.bandeja-lista ::deep \.bandeja-fila-linea3 > :not\(\.bandeja-fila-preview\)");
        regla.Should().MatchRegex(@"flex-shrink\s*:\s*0");
        regla.Should().MatchRegex(@"white-space\s*:\s*nowrap");
    }

    [Fact]
    public void El_documento_citado_apila_nombre_y_meta_y_deja_envolver_el_nombre()
    {
        var css = Leer(Bandeja);
        Regla(css, @"\.bandeja-documento-citado").Should().MatchRegex(@"flex-direction\s*:\s*column");
        Regla(css, @"\.bandeja-documento-citado-nombre").Should().NotMatchRegex(@"white-space\s*:\s*nowrap");
        Regla(css, @"\.bandeja-documento-citado-meta").Should().NotMatchRegex(@"flex-shrink\s*:\s*0");
    }

    [Fact]
    public void El_input_de_archivos_del_composer_se_oculta_con_deep()
    {
        var css = Leer(Composer);
        Regla(css, @"\.composer-boton-adjuntar ::deep input\[type=""file""\]").Should().MatchRegex(@"opacity\s*:\s*0");
        Regex.IsMatch(css, @"(?m)^\.composer-boton-adjuntar input\[type=""file""\]").Should().BeFalse(
            "sin ::deep la regla no alcanza al <input> del componente InputFile");
    }

    /// <summary>Cuerpo de la regla cuyo selector es exactamente <paramref name="selector"/>. Falla si no existe (control positivo).</summary>
    private static string Regla(string css, string selector)
    {
        var m = Regex.Match(css, @"(?m)^" + selector + @"\s*\{(?<c>[^}]*)\}");
        m.Success.Should().BeTrue($"la regla «{selector}» debe existir");
        return Regex.Replace(m.Groups["c"].Value, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }

    private static string Leer(string relativa)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio para leer la hoja de estilos");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", relativa));
    }
}
