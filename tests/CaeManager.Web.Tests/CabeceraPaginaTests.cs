using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato de <see cref="CabeceraPagina"/>, la cabecera de página Gen 2.
///
/// <para>
/// Lo que estas pruebas SÍ observan: qué elementos salen en el DOM, con qué
/// clases, y que las tres partes opcionales —kicker, descripción y acciones—
/// no dejen un hueco vacío cuando no se pasan. Un <c>span</c> de kicker vacío
/// no rompe nada y añade separación fantasma sobre el título en 20 pantallas;
/// es justo el tipo de defecto mudo que motivó esta primitiva.
/// </para>
///
/// <para>
/// Lo que NO observan, y hay que decirlo: el estilo calculado. El tamaño de
/// la descripción (--font-body-lg, 16/24) y el del kicker (--font-body-sm con
/// ls .08em) viven en <c>CabeceraPagina.razor.css</c>, y bUnit no aplica CSS.
/// Que las clases estén puestas no demuestra que se vean como el mockup —
/// eso se comprueba en el navegador, y así queda anotado.
/// </para>
/// </summary>
public class CabeceraPaginaTests : BunitContext
{
    [Fact]
    public void El_titulo_sale_como_h1_de_pagina()
    {
        var cut = Render<CabeceraPagina>(p => p.Add(c => c.Titulo, "Proyectos"));

        var h1 = cut.Find("h1");

        h1.TextContent.Trim().Should().Be("Proyectos");
        h1.GetAttribute("class").Should().Contain("titulo-pagina");
    }

    [Fact]
    public void Integrada_en_el_hub_el_titulo_baja_a_h2_para_no_duplicar_el_h1_de_la_pagina()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Retención de datos")
            .Add(c => c.Integrada, true));

        cut.FindAll("h1").Should().BeEmpty(
            "el h1 de la página lo pone el hub de Configuración; un segundo h1 rompe la jerarquía de encabezados");

        var h2 = cut.Find("h2");
        h2.TextContent.Trim().Should().Be("Retención de datos");
        h2.GetAttribute("class").Should().Contain("titulo-panel-configuracion");
    }

    [Fact]
    public void Sin_kicker_sin_descripcion_y_sin_acciones_no_queda_ningun_hueco_vacio()
    {
        var cut = Render<CabeceraPagina>(p => p.Add(c => c.Titulo, "Vehículos"));

        cut.FindAll(".cabecera-pagina-kicker").Should().BeEmpty();
        cut.FindAll(".cabecera-pagina-descripcion").Should().BeEmpty();
        cut.FindAll(".acciones-cabecera").Should().BeEmpty();
        cut.FindAll(".cabecera-pagina-con-descripcion").Should().BeEmpty(
            "el modificador que alinea las acciones arriba solo tiene sentido cuando hay entradilla");
    }

    [Fact]
    public void Un_kicker_en_blanco_no_pinta_el_span()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Alertas")
            .Add(c => c.Kicker, "   "));

        cut.FindAll(".cabecera-pagina-kicker").Should().BeEmpty(
            "una cadena de espacios es ausencia de kicker, no un kicker vacío que añade separación");
    }

    [Fact]
    public void El_kicker_se_pinta_antes_del_titulo()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Mi trabajo")
            .Add(c => c.Kicker, "Control"));

        var texto = cut.Find(".cabecera-pagina-texto");
        var hijos = texto.Children.ToList();

        hijos[0].GetAttribute("class").Should().Contain("cabecera-pagina-kicker");
        hijos[0].TextContent.Trim().Should().Be("Control");
        hijos[1].TagName.Should().Be("H1");
    }

    [Fact]
    public void La_descripcion_va_en_un_parrafo_bajo_el_titulo_y_admite_marcado()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Conexiones de integración")
            .Add(c => c.Descripcion, (Microsoft.AspNetCore.Components.RenderFragment)(b =>
            {
                b.AddMarkupContent(0, "Usa el <a href=\"/comunicaciones/buzon\">explorador técnico</a>.");
            })));

        var p = cut.Find("p.cabecera-pagina-descripcion");

        p.QuerySelector("a")!.GetAttribute("href").Should().Be("/comunicaciones/buzon",
            "varias descripciones llevan enlaces dentro: por eso el parámetro es RenderFragment y no string");

        var texto = cut.Find(".cabecera-pagina-texto");
        var hijos = texto.Children.ToList();
        hijos[0].TagName.Should().Be("H1");
        hijos[1].GetAttribute("class").Should().Contain("cabecera-pagina-descripcion");
    }

    [Fact]
    public void Con_descripcion_la_cabecera_marca_el_modificador_que_sube_las_acciones()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Retención de datos")
            .Add(c => c.Descripcion, (Microsoft.AspNetCore.Components.RenderFragment)(b => b.AddContent(0, "Texto"))));

        cut.Find("header").GetAttribute("class").Should()
            .Contain("cabecera-pagina").And.Contain("cabecera-pagina-con-descripcion");
    }

    [Fact]
    public void Las_acciones_van_fuera_del_bloque_de_texto_para_que_el_flex_las_lleve_a_la_derecha()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Delegaciones")
            .Add(c => c.Acciones, (Microsoft.AspNetCore.Components.RenderFragment)(b =>
            {
                b.OpenElement(0, "button");
                b.AddContent(1, "Nueva delegación");
                b.CloseElement();
            })));

        var acciones = cut.Find(".acciones-cabecera");

        acciones.QuerySelector("button")!.TextContent.Should().Be("Nueva delegación");
        acciones.ParentElement!.GetAttribute("class").Should().Contain("cabecera-pagina");
        cut.Find(".cabecera-pagina-texto").QuerySelector("button").Should().BeNull(
            "dentro del bloque de texto el botón quedaría bajo el título, no a su derecha");
    }

    /// <summary>
    /// Las acciones pueden no pintar nada —Delegaciones solo ofrece «Nueva
    /// delegación» al administrador de plataforma—, y aun así el envoltorio
    /// existe. Se documenta como es, en vez de dar por hecho lo contrario.
    /// </summary>
    [Fact]
    public void Un_fragmento_de_acciones_que_no_pinta_nada_deja_el_envoltorio_vacio()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Delegaciones")
            .Add(c => c.Acciones, (Microsoft.AspNetCore.Components.RenderFragment)(_ => { })));

        cut.Find(".acciones-cabecera").TextContent.Trim().Should().BeEmpty();
    }

    /// <summary>
    /// La pieza que acompaña al título —el badge de estado del editor de
    /// plantillas— va en su misma línea, no bajo él: por eso el h1 pasa a vivir
    /// dentro de un envoltorio junto a ella.
    /// </summary>
    [Fact]
    public void La_pieza_junto_al_titulo_comparte_linea_con_el_h1()
    {
        var cut = Render<CabeceraPagina>(p => p
            .Add(c => c.Titulo, "Anexo II — Información de riesgos")
            .Add(c => c.JuntoAlTitulo, (Microsoft.AspNetCore.Components.RenderFragment)(b =>
            {
                b.OpenElement(0, "span");
                b.AddAttribute(1, "class", "badge");
                b.AddContent(2, "Confirmada");
                b.CloseElement();
            })));

        var titular = cut.Find(".cabecera-pagina-titular");
        var hijos = titular.Children.ToList();

        hijos[0].TagName.Should().Be("H1");
        hijos[0].TextContent.Trim().Should().Be("Anexo II — Información de riesgos");
        hijos[1].TextContent.Trim().Should().Be("Confirmada");
        titular.ParentElement!.GetAttribute("class").Should().Contain("cabecera-pagina-texto");
    }

    /// <summary>
    /// Sin esa pieza el título se pinta como siempre, suelto: envolverlo igual
    /// metería una caja flex de más en las pantallas que ya usan la primitiva.
    /// </summary>
    [Fact]
    public void Sin_pieza_junto_al_titulo_el_h1_no_se_envuelve()
    {
        var cut = Render<CabeceraPagina>(p => p.Add(c => c.Titulo, "Vehículos"));

        cut.FindAll(".cabecera-pagina-titular").Should().BeEmpty();
        cut.Find(".cabecera-pagina-texto").Children[0].TagName.Should().Be("H1");
    }
}
