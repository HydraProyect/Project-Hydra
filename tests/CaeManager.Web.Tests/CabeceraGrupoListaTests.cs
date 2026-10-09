using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cabecera de grupo de un listado agrupado (pieza común de la línea «agrupar filas»): blanca de serie y
/// tintada solo si la pantalla lo pide, total del grupo entero con «n de N» cuando la página enseña parte,
/// resumen por estado con punto de color, y botón con aria-expanded salvo que el grupo esté abierto por algo
/// ajeno a él. No sabe de qué es el listado: aquí no hay ni un Centro.
/// </summary>
public class CabeceraGrupoListaTests : BunitContext
{
    private int _alternancias;

    public CabeceraGrupoListaTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<CabeceraGrupoLista> Renderizar(
        int total = 12, int? enPagina = null, IReadOnlyList<ResumenEstadoGrupo>? resumen = null,
        TinteGrupoLista tinte = TinteGrupoLista.Ninguno, bool abierto = false, bool fijo = false, string? tituloContador = null) =>
        Render<CabeceraGrupoLista>(p => p
            .Add(c => c.Nombre, "Astilleros Orion")
            .Add(c => c.Total, total)
            .Add(c => c.EnPagina, enPagina)
            .Add(c => c.TituloContador, tituloContador)
            .Add(c => c.Resumen, resumen ?? [])
            .Add(c => c.Tinte, tinte)
            .Add(c => c.Abierto, abierto)
            .Add(c => c.Fijo, fijo)
            .Add(c => c.OnAlternar, EventCallback.Factory.Create(this, () => _alternancias++)));

    [Fact]
    public void Sin_tinte_la_cabecera_es_blanca_y_dice_el_nombre_y_el_total()
    {
        var cut = Renderizar(total: 12, tituloContador: "12 centro(s)");

        var grupo = cut.Find(".grupo-lista");
        grupo.ClassList.Should().NotContain(c => c.StartsWith("fila-tintada-", StringComparison.Ordinal),
            "blanca de serie: el tinte solo lo pone un grupo con problema");
        grupo.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim().Should().Be("Astilleros Orion");
        var contador = grupo.QuerySelector(".grupo-lista-contador")!;
        contador.TextContent.Trim().Should().Be("12");
        contador.GetAttribute("title").Should().Be("12 centro(s)");
        grupo.QuerySelectorAll(".grupo-lista-resumen").Should().BeEmpty("sin resumen no se pinta su hueco");
    }

    [Theory]
    [InlineData(TinteGrupoLista.Peligro, "fila-tintada-peligro")]
    [InlineData(TinteGrupoLista.Aviso, "fila-tintada-aviso")]
    public void Con_tinte_lleva_la_misma_clase_que_tine_una_fila_con_problema(TinteGrupoLista tinte, string clase)
    {
        var cut = Renderizar(tinte: tinte);

        cut.Find(".grupo-lista").ClassList.Where(c => c.StartsWith("fila-tintada-", StringComparison.Ordinal)).Should().Equal(clase);
        cut.Find(".grupo-lista").Children.Should().ContainSingle(hijo => hijo.ClassList.Contains("grupo-lista-cabecera"),
            "el degradado de list-page.css cuelga de «.grupo-lista.fila-tintada-* > .grupo-lista-cabecera»");
    }

    /// <summary>El resumen llega redactado: la cabecera pinta cada entrada con su punto, en el orden recibido.</summary>
    [Fact]
    public void El_resumen_pinta_un_punto_del_tono_y_el_texto_de_cada_estado_en_su_orden()
    {
        var cut = Renderizar(resumen:
        [
            new(TonoBadge.Peligro, "2 vencidos"),
            new(TonoBadge.Advertencia, "1 por vencer"),
        ]);

        var estados = cut.FindAll(".grupo-lista-resumen .grupo-lista-resumen-estado");
        estados.Select(e => e.TextContent.Trim()).Should().Equal("2 vencidos", "1 por vencer");
        estados.Select(e => e.QuerySelector(".grupo-lista-punto")!.ClassName).Should().Equal(
            "grupo-lista-punto grupo-lista-punto-peligro", "grupo-lista-punto grupo-lista-punto-advertencia");
        estados.Select(e => e.QuerySelector(".grupo-lista-punto")!.GetAttribute("aria-hidden")).Should().AllBe("true",
            "el punto es decoración: el estado ya lo dice el texto");
        cut.FindAll(".badge").Should().BeEmpty("el resumen ya no son pastillas «N con problema»");
    }

    [Fact]
    public void Contraida_es_un_boton_que_anuncia_que_esta_cerrado_y_avisa_al_pulsarlo()
    {
        var cut = Renderizar(abierto: false);

        var cabecera = cut.Find("button.grupo-lista-cabecera");
        cabecera.GetAttribute("aria-expanded").Should().Be("false");
        cabecera.ClassList.Should().NotContain("grupo-lista-cabecera-abierta");

        cabecera.Click();

        _alternancias.Should().Be(1);
    }

    [Fact]
    public void Abierta_es_un_boton_que_anuncia_que_esta_abierto()
    {
        var cut = Renderizar(abierto: true);

        var cabecera = cut.Find("button.grupo-lista-cabecera");
        cabecera.GetAttribute("aria-expanded").Should().Be("true");
        cabecera.ClassList.Should().Contain("grupo-lista-cabecera-abierta");
    }

    /// <summary>Abierta por algo ajeno al grupo: no puede contraerlo, así que ni es un botón ni lo anuncia.</summary>
    [Fact]
    public void Fija_no_es_un_boton_ni_lleva_aria_expanded()
    {
        var cut = Renderizar(fijo: true);

        cut.FindAll("button").Should().BeEmpty();
        var cabecera = cut.Find(".grupo-lista-cabecera");
        cabecera.HasAttribute("aria-expanded").Should().BeFalse();
        cabecera.ClassList.Should().Contain(["grupo-lista-cabecera-fija", "grupo-lista-cabecera-abierta"]);
    }

    /// <summary>El grupo sigue en otra página: el contador dice cuántas de sus filas hay en esta.</summary>
    [Fact]
    public void Si_la_pagina_solo_ensena_parte_del_grupo_el_contador_dice_n_de_N()
    {
        var cut = Renderizar(total: 12, enPagina: 3, tituloContador: "12 centro(s)");

        var contador = cut.Find(".grupo-lista-contador");
        contador.TextContent.Trim().Should().Be("3 de 12");
        contador.GetAttribute("title").Should().Be("3 de 12 en esta página");
    }

    [Theory]
    [InlineData(12)]
    [InlineData(null)]
    public void Con_el_grupo_entero_en_la_pagina_el_contador_solo_dice_el_total(int? enPagina)
    {
        var cut = Renderizar(total: 12, enPagina: enPagina);

        cut.Find(".grupo-lista-contador").TextContent.Trim().Should().Be("12");
    }
}
