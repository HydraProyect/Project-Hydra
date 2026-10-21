using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Grupos plegables de los listados con «Agrupar» (bloque 1 de CAMBIOS-SESION-2026-10-21): la cabecera es un botón con
/// aria-expanded, dice «· N» y, plegada y con filas en rojo, lleva la pastilla roja «N con incidencias»; todos los grupos
/// nacen plegados y los abiertos se recuerdan; los grupos se calculan sobre TODAS las filas visibles (el tamaño de página
/// no los corta). No sabe de qué es el listado.
/// </summary>
public class GruposPlegablesDeListaTests : BunitContext
{
    private int _alternancias;

    public GruposPlegablesDeListaTests()
    {
        Services.AddLocalization();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<CabeceraGrupoLista> Renderizar(int total = 5, int incidencias = 0, bool abierto = false, bool fijo = false) =>
        Render<CabeceraGrupoLista>(p => p
            .Add(c => c.Nombre, "Astilleros Orion")
            .Add(c => c.Total, total)
            .Add(c => c.Incidencias, incidencias)
            .Add(c => c.Abierto, abierto)
            .Add(c => c.Fijo, fijo)
            .Add(c => c.OnAlternar, EventCallback.Factory.Create(this, () => _alternancias++)));

    [Fact]
    public void La_cabecera_plegada_es_un_boton_con_aria_expanded_false_y_dice_nombre_y_mas_N()
    {
        var cut = Renderizar(total: 5);

        var boton = cut.Find("button.grupo-lista-cabecera");
        boton.GetAttribute("aria-expanded").Should().Be("false");
        cut.Find(".grupo-lista-nombre").TextContent.Trim().Should().Be("Astilleros Orion");
        cut.Find(".grupo-lista-contador").TextContent.Trim().Should().Be("· 5");
    }

    [Fact]
    public void El_chevron_esta_en_la_cabecera_y_la_abierta_cambia_su_clase_para_girarlo()
    {
        var plegada = Renderizar(abierto: false);
        var abierta = Renderizar(abierto: true);

        plegada.Find(".grupo-lista-chevron").Should().NotBeNull();
        plegada.Find("button.grupo-lista-cabecera").ClassList.Should().NotContain("grupo-lista-cabecera-abierta",
            "plegada, el chevron queda a -90 grados (list-page.css)");
        abierta.Find("button.grupo-lista-cabecera").ClassList.Should().Contain("grupo-lista-cabecera-abierta");
        abierta.Find("button.grupo-lista-cabecera").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public void Plegada_con_filas_en_rojo_lleva_la_pastilla_con_incidencias()
    {
        var cut = Renderizar(total: 5, incidencias: 3);

        cut.Find(".grupo-lista-incidencias").TextContent.Trim().Should().Be("3 con incidencias");
    }

    [Fact]
    public void Con_una_sola_fila_en_rojo_la_pastilla_va_en_singular()
    {
        var cut = Renderizar(total: 5, incidencias: 1);

        cut.Find(".grupo-lista-incidencias").TextContent.Trim().Should().Be("1 con incidencia");
    }

    [Fact]
    public void Abierta_o_sin_filas_en_rojo_no_hay_pastilla()
    {
        Renderizar(incidencias: 3, abierto: true).FindAll(".grupo-lista-incidencias").Should().BeEmpty("abierto, las filas rojas ya se ven");
        Renderizar(incidencias: 0, abierto: false).FindAll(".grupo-lista-incidencias").Should().BeEmpty("sin filas en rojo no hay nada que avisar");
    }

    [Fact]
    public void Pulsar_la_cabecera_avisa_para_alternar_el_grupo()
    {
        var cut = Renderizar();

        cut.Find("button.grupo-lista-cabecera").Click();

        _alternancias.Should().Be(1);
    }

    [Fact]
    public void Los_grupos_abiertos_se_recuerdan_y_todos_nacen_plegados()
    {
        var abiertos = new GruposAbiertos();
        abiertos.EstaAbierto("a").Should().BeFalse("todos los grupos empiezan plegados");

        abiertos.Alternar("a");
        abiertos.Alternar("b");
        abiertos.Alternar("b");

        abiertos.EstaAbierto("a").Should().BeTrue();
        abiertos.EstaAbierto("b").Should().BeFalse("alternar dos veces lo vuelve a plegar");
        abiertos.Cantidad.Should().Be(1);
    }

    private sealed record Fila(string Grupo, bool Roja);

    private static List<FilaDeGrupo<Fila>> Agrupar(IEnumerable<Fila> filas, GruposAbiertos abiertos, bool agrupado = true) =>
        [.. GruposDeLista.Agrupar(filas, agrupado, f => f.Grupo, (clave, _) => clave, f => f.Roja, abiertos)];

    /// <summary>
    /// El tamaño de página no corta los grupos: se agrupan todas las filas visibles. 130 filas, más que cualquier
    /// tamaño de página (20/50/100), y cada grupo dice su total entero.
    /// </summary>
    [Fact]
    public void Los_grupos_se_calculan_sobre_todas_las_filas_aunque_superen_el_tamano_de_pagina()
    {
        var filas = Enumerable.Range(0, 130).Select(i => new Fila(i < 70 ? "A" : "B", i % 10 == 0)).ToList();

        var resultado = Agrupar(filas, new GruposAbiertos());

        resultado.Should().HaveCount(130);
        var cabeceras = resultado.Where(f => f.Cabecera is not null).Select(f => f.Cabecera!).ToList();
        cabeceras.Select(c => (c.Clave, c.Total)).Should().Equal([("A", 70), ("B", 60)]);
        cabeceras.Select(c => c.Incidencias).Should().Equal([7, 6]);
        AgrupacionDeLista.TamanoPedido(agrupado: true, tamanoElegido: 20).Should().BeGreaterThan(130, "agrupada se piden todas las filas");
        AgrupacionDeLista.TamanoPedido(agrupado: false, tamanoElegido: 20).Should().Be(20);
    }

    [Fact]
    public void Plegado_oculta_las_filas_del_grupo_y_abierto_las_muestra()
    {
        var abiertos = new GruposAbiertos();
        var filas = new[] { new Fila("A", false), new Fila("A", true), new Fila("B", false) };

        // Todos los grupos nacen plegados: ninguna fila se pinta.
        Agrupar(filas, abiertos).Select(f => f.Oculta).Should().Equal(true, true, true);

        abiertos.Alternar("A");
        var abierto = Agrupar(filas, abiertos);
        abierto.Select(f => f.Oculta).Should().Equal(false, false, true);
        // La cabecera va solo en la primera fila de cada grupo.
        abierto.Where(f => f.Cabecera is not null).Select(f => f.Cabecera!.Clave).Should().Equal("A", "B");
    }

    [Fact]
    public void Sin_agrupar_las_filas_van_tal_cual_sin_cabecera_y_sin_ocultar()
    {
        var resultado = Agrupar([new Fila("A", true), new Fila("B", false)], new GruposAbiertos(), agrupado: false);

        resultado.Should().OnlyContain(f => f.Cabecera == null && !f.Oculta);
    }
}
