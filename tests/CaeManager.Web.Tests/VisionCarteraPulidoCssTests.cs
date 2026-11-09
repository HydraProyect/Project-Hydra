using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato del CSS de la Visión de cartera (demo 2026-11-10). bUnit no aplica CSS: estos tests leen el fichero y fijan las reglas
/// que arreglan lo medido a 1024 px y a 375 px. Comprueban que la regla existe y es la correcta, no cómo la pinta un navegador.
/// </summary>
public class VisionCarteraPulidoCssTests
{
    private static string Css()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;

        directorio.Should().NotBeNull("hay que encontrar la raíz del repositorio para leer el CSS");
        return File.ReadAllText(Path.Combine(directorio!.FullName, "src", "CaeManager.Web", "Features", "VisionCartera", "Pages", "VisionCartera.razor.css"));
    }

    /// <summary>El cuerpo de la primera regla cuyo selector es exactamente <paramref name="selector"/>.</summary>
    private static string Regla(string css, string selector)
    {
        var inicio = css.IndexOf(selector + " {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, $"debe existir la regla «{selector}»");
        var fin = css.IndexOf('}', inicio);
        return css[(inicio + selector.Length)..fin];
    }

    /// <summary>B1: la tabla accesible es absoluta y no se encoge a 1 px; su caja contenedora la recorta o la página desborda.</summary>
    [Fact]
    public void El_reparto_contiene_su_tabla_accesible_para_que_la_pagina_no_desborde()
    {
        var css = Css();

        var figura = Regla(css, "figure.reparto-riesgo");
        figura.Should().Contain("position: relative").And.Contain("overflow: hidden");
        Regla(css, ".reparto-riesgo-datos").Should().Contain("position: absolute");
    }

    /// <summary>B1 a 375 px: la tabla de organizaciones desliza dentro de su tarjeta, nunca en la página.</summary>
    [Fact]
    public void La_tabla_de_organizaciones_desliza_dentro_de_su_tarjeta()
    {
        Regla(Css(), ".vision-cartera-contenido ::deep .tarjeta-organizaciones-riesgo > .tarjeta-contenido")
            .Should().Contain("overflow-x: auto");
    }

    /// <summary>B2: filas completas; con ancho de sobra, las cinco en una fila y sin envolver.</summary>
    [Fact]
    public void Las_tarjetas_de_riesgo_reparten_la_ultima_fila_y_con_ancho_caben_las_cinco_en_una()
    {
        var css = Css();

        var rejilla = Regla(css, ".vision-cartera-contenido ::deep .rejilla-kpis-cartera");
        rejilla.Should().Contain("display: flex").And.Contain("flex-wrap: wrap");
        Regla(css, ".vision-cartera-contenido ::deep .rejilla-kpis-cartera > .tarjeta-metrica").Should().Contain("flex: 1 1");
        Regla(css, ".seccion-cartera").Should().Contain("container-type: inline-size");
        css.Should().Contain("@container (min-width: 860px)");
    }

    /// <summary>B3: el ámbito de la cabecera va a la izquierda y solo se alinea a la derecha con ancho de sobra; la nota de sección sigue a la derecha del título mientras cabe (maqueta).</summary>
    [Fact]
    public void Los_subtitulos_van_a_la_izquierda_y_solo_a_la_derecha_con_ancho_de_sobra()
    {
        var css = Css();

        var ambito = Regla(css, ".ambito-cartera");
        ambito.Should().Contain("align-items: flex-start").And.Contain("text-align: left");
        ambito.Should().NotContain("flex-end").And.NotContain("text-align: right");

        // La nota de sección sigue a la derecha del título mientras cabe (maqueta) y envuelve a la izquierda cuando no.
        Regla(css, ".cabecera-seccion-cartera").Should().Contain("justify-content: space-between").And.Contain("flex-wrap: wrap");

        var inicio = css.IndexOf("@media (min-width: 1280px) {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0);
        var apertura = css.IndexOf('{', inicio);
        var fin = css.IndexOf("\n}", apertura, StringComparison.Ordinal);
        var bloque = css[apertura..fin];
        bloque.Should().Contain(".ambito-cartera").And.Contain("align-items: flex-end").And.Contain("text-align: right");
    }
}
