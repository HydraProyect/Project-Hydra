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

    /// <summary>B3: el ámbito de la cabecera va a la izquierda, alineado con el título, a cualquier ancho (a 1440 px estaba a la derecha bajo un h1 a la izquierda); la nota de sección sigue a la derecha del título mientras cabe (maqueta).</summary>
    [Fact]
    public void El_ambito_de_la_cabecera_va_bajo_el_titulo_y_alineado_a_la_izquierda_a_cualquier_ancho()
    {
        var css = Css();

        var ambito = Regla(css, ".ambito-cartera");
        ambito.Should().Contain("align-items: flex-start").And.Contain("text-align: left");
        ambito.Should().NotContain("flex-end").And.NotContain("text-align: right");

        // La caja de acciones ocupa su propia línea: baja bajo el título en vez de ir al borde derecho.
        Regla(css, ".contenedor-pagina ::deep .acciones-cabecera").Should().Contain("flex: 0 0 100%");

        // Ninguna regla alinea a la derecha el ámbito, ni siquiera con ancho de sobra.
        css.Should().NotContain("@media (min-width: 1280px)");

        // La nota de sección sigue a la derecha del título mientras cabe (maqueta) y envuelve a la izquierda cuando no.
        Regla(css, ".cabecera-seccion-cartera").Should().Contain("justify-content: space-between").And.Contain("flex-wrap: wrap");
    }

    /// <summary>Defecto 1 (375 px): en estrecho la tabla de organizaciones se apila en fichas; las cifras y la acción quedan alcanzables sin scroll horizontal.</summary>
    [Fact]
    public void En_estrecho_la_tabla_de_organizaciones_se_apila_en_fichas()
    {
        var css = Css();

        var inicio = css.IndexOf("@container (max-width: 640px) {", StringComparison.Ordinal);
        inicio.Should().BeGreaterThanOrEqualTo(0, "la tabla se apila por ancho de la sección (container query)");
        var bloque = css[inicio..];

        bloque.Should().Contain(".tabla-organizaciones tr").And.Contain("display: grid");
        bloque.Should().Contain(".tabla-organizaciones td[data-etiqueta]::before").And.Contain("content: attr(data-etiqueta)");
        bloque.Should().Contain(".tabla-organizaciones thead").And.Contain("clip-path: inset(50%)", "la cabecera se oculta a la vista, no del lector de pantalla");
        bloque.Should().Contain(".tabla-organizaciones td.celda-accion-organizacion").And.Contain("grid-column: 1 / -1");
    }

    /// <summary>Defecto 2 (1024 px): las tarjetas de actividad solo van en tres columnas con ancho para su etiqueta; sin elipsis, la etiqueta envuelve.</summary>
    [Fact]
    public void La_actividad_general_solo_va_en_tres_columnas_con_ancho_para_la_etiqueta()
    {
        var css = Css();

        Regla(css, ".actividad-cartera").Should().Contain("container-type: inline-size");
        Regla(css, ".rejilla-actividad-cartera").Should().Contain("repeat(2, minmax(0, 1fr))");
        css.Should().Contain("@container (min-width: 520px)");
        Regla(css, ".rejilla-actividad-cartera ::deep .tarjeta-metrica-etiqueta").Should().Contain("overflow-wrap: anywhere").And.NotContain("ellipsis");
    }

    /// <summary>Defecto 3 (1440 px): el gráfico del reparto no encoge sus etiquetas; van con el token de pie y el lienzo desliza.</summary>
    [Fact]
    public void Las_etiquetas_del_reparto_usan_el_token_de_pie_y_el_lienzo_desliza()
    {
        var css = Css();

        var etiquetas = Regla(css, ".reparto-riesgo-sin-riesgo");
        etiquetas.Should().Contain("font: var(--font-caption)");
        css.Should().NotContain("font-size: 10px");
        Regla(css, ".reparto-riesgo-lienzo").Should().Contain("overflow-x: auto");
    }
}