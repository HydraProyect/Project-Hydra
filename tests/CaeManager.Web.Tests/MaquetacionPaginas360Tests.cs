using System.Globalization;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contrato de la maquetación compartida de las páginas 360 (Centro 360,
/// Cliente 360, Empresa 360): <see cref="CuerpoConLateral"/>,
/// <see cref="Tarjeta"/> compacta, <see cref="RejillaDatos"/> /
/// <see cref="DatoRejilla"/>, <see cref="ListaLateral"/> /
/// <see cref="ElementoListaLateral"/>, <see cref="ListaRelaciones"/> /
/// <see cref="FilaRelacion"/> y <see cref="Boton360"/>.
///
/// <para>
/// Lo que SÍ observan: la estructura del DOM (qué elemento, con qué clase y
/// qué atributos), que las partes opcionales no dejen huecos, y que el botón
/// 360 abra lo que dice abrir y lo anuncie con el nombre de la entidad en el
/// idioma de la cuenta. Lo que NO observan: el estilo calculado (bUnit no
/// aplica CSS). Las medidas de los .razor.css se copiaron de los mockups y de
/// CentroDetalle.razor.css; que se vean igual se comprueba en el navegador.
/// </para>
/// </summary>
public class MaquetacionPaginas360Tests : BunitContext
{
    public MaquetacionPaginas360Tests()
    {
        Services.AddLocalization();
    }

    // ── Boton360 ───────────────────────────────────────────────────────────

    [Fact]
    public void El_boton_360_nombra_la_entidad_dibuja_el_icono_y_avisa_al_pulsarlo()
    {
        var pulsado = 0;
        var cut = Render<Boton360>(p => p
            .Add(b => b.Nombre, "Montajes Ebro S.L.")
            .Add(b => b.OnClick, () => pulsado++));

        var boton = cut.Find("button.boton-360");
        boton.GetAttribute("type").Should().Be("button");
        boton.GetAttribute("aria-label").Should().Be("Consultar Montajes Ebro S.L. de un vistazo",
            "con varios botones 360 en la misma pantalla, un nombre accesible genérico no dice cuál abre qué");
        boton.GetAttribute("title").Should().Be("Consultar de un vistazo");
        boton.QuerySelectorAll("svg path, svg circle").Should().NotBeEmpty(
            "un nombre de icono desconocido pinta un svg vacío y el botón quedaría en blanco");

        boton.Click();

        pulsado.Should().Be(1);
    }

    /// <summary>
    /// Los textos salen de TextosComunes y no están escritos en el componente:
    /// en ca-ES se leen del recurso satélite. El valor catalán difiere del
    /// español a propósito —si fueran idénticos, el test no distinguiría un
    /// texto localizado de uno escrito a mano—.
    /// </summary>
    [Fact]
    public void En_catalan_el_boton_360_se_anuncia_con_el_recurso_ca_ES()
    {
        var (previa, previaUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ca-ES");
        try
        {
            var cut = Render<Boton360>(p => p.Add(b => b.Nombre, "Montajes Ebro S.L."));

            var boton = cut.Find("button.boton-360");
            boton.GetAttribute("aria-label").Should().Be("Consultar Montajes Ebro S.L. d’un cop d’ull");
            boton.GetAttribute("title").Should().Be("Consultar d’un cop d’ull");
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (previa, previaUi);
        }
    }

    // ── CuerpoConLateral ───────────────────────────────────────────────────

    [Fact]
    public void El_cuerpo_pone_la_columna_principal_y_el_lateral_en_un_aside_con_nombre()
    {
        var cut = Render<CuerpoConLateral>(p => p
            .Add(c => c.EtiquetaLateral, "Contexto del cliente")
            .AddChildContent("<p id='principal'>Pestañas</p>")
            .Add(c => c.Lateral, (RenderFragment)(b => b.AddMarkupContent(0, "<p id='lateral'>Información</p>"))));

        var cuerpo = cut.Find(".cuerpo-con-lateral");
        cuerpo.ClassList.Should().NotContain("cuerpo-con-lateral-sin-lateral");
        cut.Find(".cuerpo-con-lateral-principal #principal").Should().NotBeNull();
        var lateral = cut.Find("aside.cuerpo-con-lateral-lateral");
        lateral.GetAttribute("aria-label").Should().Be("Contexto del cliente");
        lateral.QuerySelector("#lateral").Should().NotBeNull();
    }

    [Fact]
    public void Sin_lateral_no_queda_un_aside_vacio_y_la_principal_ocupa_todo_el_ancho()
    {
        var cut = Render<CuerpoConLateral>(p => p.AddChildContent("<p>Pestañas</p>"));

        cut.FindAll("aside").Should().BeEmpty();
        cut.Find(".cuerpo-con-lateral").ClassList.Should().Contain("cuerpo-con-lateral-sin-lateral",
            "sin esa clase la rejilla reservaría 300 px vacíos a la derecha");
    }

    // ── Tarjeta compacta ───────────────────────────────────────────────────

    [Fact]
    public void La_tarjeta_compacta_marca_su_variante_y_pone_la_accion_en_la_cabecera()
    {
        var cut = Render<Tarjeta>(p => p
            .Add(t => t.Titulo, "Información")
            .Add(t => t.Compacta, true)
            .Add(t => t.AccionesHeader, (RenderFragment)(b => b.AddMarkupContent(0, "<button id='editar'>Editar →</button>")))
            .AddChildContent("<p>Contenido</p>"));

        cut.Find(".tarjeta").ClassList.Should().Contain("tarjeta-compacta");
        cut.Find(".tarjeta-header h2.tarjeta-titulo").TextContent.Should().Be("Información");
        cut.Find(".tarjeta-header .tarjeta-acciones #editar").Should().NotBeNull();
    }

    [Fact]
    public void Sin_Compacta_la_tarjeta_de_siempre_no_cambia()
    {
        var cut = Render<Tarjeta>(p => p.Add(t => t.Titulo, "Resumen").AddChildContent("<p>Contenido</p>"));

        cut.Find(".tarjeta").ClassList.Should().NotContain("tarjeta-compacta",
            "la variante es opcional: los 18 consumidores de Tarjeta no la piden");
    }

    // ── RejillaDatos / DatoRejilla ─────────────────────────────────────────

    [Fact]
    public void La_rejilla_es_una_lista_de_descripcion_con_un_par_etiqueta_valor_por_dato()
    {
        var cut = Render<RejillaDatos>(p => p.AddChildContent<DatoRejilla>(d => d
            .Add(x => x.Etiqueta, "CNAE")
            .Add(x => x.Valor, "4322")));

        var dato = cut.Find("dl.rejilla-datos > div.dato-rejilla");
        dato.QuerySelector("dt.dato-rejilla-etiqueta")!.TextContent.Should().Be("CNAE");
        dato.QuerySelector("dd.dato-rejilla-valor")!.TextContent.Trim().Should().Be("4322");
        dato.ClassList.Should().NotContain("dato-rejilla-ancho");
    }

    [Fact]
    public void Un_dato_ancho_ocupa_las_dos_columnas_y_uno_sin_valor_dice_que_esta_vacio()
    {
        var ancho = Render<DatoRejilla>(p => p
            .Add(x => x.Etiqueta, "Convenio aplicable")
            .Add(x => x.Valor, "Metal de Zaragoza")
            .Add(x => x.Ancho, true));
        ancho.Find(".dato-rejilla").ClassList.Should().Contain("dato-rejilla-ancho");

        var vacio = Render<DatoRejilla>(p => p.Add(x => x.Etiqueta, "CNAE"));
        vacio.Find("dd .dato-rejilla-vacio").TextContent.Should().Be("—",
            "un hueco en blanco no distingue «vacío» de «no ha cargado»");
    }

    [Fact]
    public void El_marcado_del_valor_manda_sobre_el_texto()
    {
        var cut = Render<DatoRejilla>(p => p
            .Add(x => x.Etiqueta, "Alta")
            .Add(x => x.Valor, "texto que no debe salir")
            .AddChildContent("<b id='marcado'>04/03/2019</b>"));

        cut.Find("dd #marcado").TextContent.Should().Be("04/03/2019");
        cut.Markup.Should().NotContain("texto que no debe salir");
    }

    // ── ListaLateral / ElementoListaLateral ────────────────────────────────

    [Fact]
    public void La_lista_lateral_pinta_cada_elemento_con_su_etiqueta_y_sus_lineas_de_detalle()
    {
        var cut = Render<ListaLateral>(p => p.AddChildContent<ElementoListaLateral>(e => e
            .Add(x => x.Etiqueta, (RenderFragment)(b => b.AddContent(0, "Usuario")))
            .AddChildContent("<span>montajesebro.cae</span><span>Proveedor</span>")));

        var elemento = cut.Find("ul.lista-lateral > li.elemento-lista-lateral");
        elemento.QuerySelector(".elemento-lista-lateral-etiqueta")!.TextContent.Should().Be("Usuario");
        elemento.QuerySelectorAll(".elemento-lista-lateral-detalles > span").Select(s => s.TextContent)
            .Should().Equal("montajesebro.cae", "Proveedor");
    }

    [Fact]
    public void Un_elemento_sin_detalle_no_deja_un_contenedor_vacio()
    {
        var cut = Render<ElementoListaLateral>(p => p
            .Add(x => x.Etiqueta, (RenderFragment)(b => b.AddContent(0, "Se accede como"))));

        cut.FindAll(".elemento-lista-lateral-detalles").Should().BeEmpty();
    }

    // ── ListaRelaciones / FilaRelacion ─────────────────────────────────────

    [Fact]
    public void La_fila_enlaza_el_nombre_a_la_pagina_360_y_su_boton_360_abre_el_panel()
    {
        var abiertos = 0;
        var cut = Render<ListaRelaciones>(p => p
            .Add(l => l.Etiqueta, "Centros de este cliente")
            .AddChildContent<FilaRelacion>(f => f
                .Add(x => x.Nombre, "Planta Barakaldo")
                .Add(x => x.Href, "/centros/5")
                .Add(x => x.NombreIcono, "centros")
                .Add(x => x.Detalle, (RenderFragment)(b => b.AddContent(0, "Empresa: Montajes Ebro S.L.")))
                .Add(x => x.Derecha, (RenderFragment)(b => b.AddMarkupContent(0, "<span id='estado'>Bloqueado</span>")))
                .Add(x => x.OnAbrir360, () => abiertos++)));

        cut.Find("ul.lista-relaciones").GetAttribute("aria-label").Should().Be("Centros de este cliente");
        var fila = cut.Find("ul.lista-relaciones > li.fila-relacion");

        var nombre = fila.QuerySelector("a.fila-relacion-nombre")!;
        nombre.TextContent.Should().Be("Planta Barakaldo");
        nombre.GetAttribute("href").Should().Be("/centros/5");
        fila.QuerySelector(".fila-relacion-icono svg").Should().NotBeNull();
        fila.QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("Empresa: Montajes Ebro S.L.");

        var derecha = fila.QuerySelector(".fila-relacion-derecha")!;
        derecha.QuerySelector("#estado").Should().NotBeNull();
        derecha.QuerySelector("button.boton-360")!.GetAttribute("aria-label")
            .Should().Be("Consultar Planta Barakaldo de un vistazo");

        cut.Find("button.boton-360").Click();

        abiertos.Should().Be(1);
    }

    [Fact]
    public void Sin_ruta_el_nombre_es_texto_y_sin_OnAbrir360_no_hay_boton_ni_columna_derecha()
    {
        var cut = Render<FilaRelacion>(p => p.Add(x => x.Nombre, "Montajes Ebro S.L."));

        cut.FindAll("a").Should().BeEmpty("sin página a la que ir, un enlace sería un enlace roto");
        cut.Find(".fila-relacion-nombre").TextContent.Should().Be("Montajes Ebro S.L.");
        cut.FindAll("button").Should().BeEmpty();
        cut.FindAll(".fila-relacion-derecha, .fila-relacion-icono, .fila-relacion-detalle").Should().BeEmpty();
    }

    /// <summary>
    /// Una entidad sin página 360 (Subcontrata en Cliente 360) pasa OnNombre:
    /// su nombre es un botón que lo llama, no un enlace ni texto muerto.
    /// </summary>
    [Fact]
    public void Con_OnNombre_y_sin_ruta_el_nombre_es_un_boton_que_lo_llama()
    {
        var pulsados = 0;
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "Andamios Cantábrico S.L.")
            .Add(x => x.OnNombre, () => pulsados++));

        cut.FindAll("a").Should().BeEmpty();
        var nombre = cut.Find("button.fila-relacion-nombre");
        nombre.GetAttribute("type").Should().Be("button", "dentro de un formulario no debe enviarlo");
        nombre.TextContent.Should().Be("Andamios Cantábrico S.L.");

        nombre.Click();

        pulsados.Should().Be(1);
    }

    /// <summary>
    /// Con Href y OnNombre a la vez gana el enlace: el parámetro es aditivo y
    /// no cambia el comportamiento de las filas que ya tienen página 360.
    /// </summary>
    [Fact]
    public void Con_ruta_el_enlace_gana_a_OnNombre()
    {
        var cut = Render<FilaRelacion>(p => p
            .Add(x => x.Nombre, "Planta Barakaldo")
            .Add(x => x.Href, "/centros/5")
            .Add(x => x.OnNombre, () => { }));

        cut.Find("a.fila-relacion-nombre").GetAttribute("href").Should().Be("/centros/5");
        cut.FindAll("button.fila-relacion-nombre").Should().BeEmpty();
    }
}
