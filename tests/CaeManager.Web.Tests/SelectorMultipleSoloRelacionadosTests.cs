using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Tests;

/// <summary>
/// El SelectorMultiple es compartido (Empresas, Subcontratas, Asignación masiva...). Estos tests fijan
/// que, sin el concepto nuevo de «relacionados» (D-20), «Solo mostrar relacionados» sigue
/// significando «solo los ya marcados».
/// </summary>
public class SelectorMultipleSoloRelacionadosTests : BunitContext
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();
    private static readonly Guid C = Guid.NewGuid();

    private IRenderedComponent<SelectorMultiple> Renderizar(IReadOnlySet<Guid>? relacionados = null) =>
        Render<SelectorMultiple>(p => p
            .Add(c => c.Elementos, [new ElementoSeleccionable(A, "Alfa"), new ElementoSeleccionable(B, "Beta"), new ElementoSeleccionable(C, "Gamma")])
            .Add(c => c.Seleccionados, new HashSet<Guid> { B })
            .Add(c => c.Relacionados, relacionados));

    private static string[] Listados(IRenderedComponent<SelectorMultiple> cut) =>
        cut.FindAll(".lista-seleccion-multiple label").Select(l => l.TextContent.Trim()).ToArray();

    [Fact]
    public async Task Por_defecto_solo_relacionados_muestra_solo_los_marcados()
    {
        var cut = Renderizar();
        Listados(cut).Should().HaveCount(3, "barrera: sin marcar el filtro se ve todo");

        await cut.Find(".selector-multiple-controles input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        Listados(cut).Should().Equal("Beta");
    }

    [Fact]
    public async Task Con_conjunto_de_relacionados_muestra_relacionados_mas_marcados()
    {
        var cut = Renderizar(new HashSet<Guid> { A });

        await cut.Find(".selector-multiple-controles input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        Listados(cut).Should().BeEquivalentTo("Alfa", "Beta");
    }

    [Fact]
    public async Task Buscar_filtra_sin_mayusculas_ni_tildes_como_el_combobox_del_kit()
    {
        var cut = Render<SelectorMultiple>(p => p
            .Add(c => c.Elementos, [new ElementoSeleccionable(A, "Construcción Peñíscola"), new ElementoSeleccionable(B, "Beta")]));

        await cut.InvokeAsync(() => cut.FindComponent<CampoTexto>().Instance.ValorChanged.InvokeAsync("construccion peniscola"));

        Listados(cut).Should().Equal("Construcción Peñíscola");
    }

    // ---- Paginación: 20 por defecto, que es lo que dan por supuesto los demás consumidores -----------------------------------

    private static IReadOnlyList<ElementoSeleccionable> Elementos(int cuantos) =>
        Enumerable.Range(1, cuantos).Select(i => new ElementoSeleccionable(Guid.NewGuid(), $"Tipo {i:000}")).ToList();

    [Fact]
    public void Por_defecto_pagina_de_20_en_20_y_con_25_elementos_hay_paginador_y_20_casillas()
    {
        var cut = Render<SelectorMultiple>(p => p.Add(c => c.Elementos, Elementos(25)));

        Listados(cut).Should().HaveCount(20);
        cut.FindComponents<PaginadorSimple>().Should().ContainSingle();
    }

    [Fact]
    public void Con_ItemsPorPagina_enorme_no_pagina_y_lista_todo()
    {
        var cut = Render<SelectorMultiple>(p => p
            .Add(c => c.Elementos, Elementos(87))
            .Add(c => c.ItemsPorPagina, int.MaxValue));

        Listados(cut).Should().HaveCount(87);
        cut.FindComponents<PaginadorSimple>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Con_ItemsPorPagina_no_positivo_no_lanza_y_trata_la_pagina_como_de_un_elemento(int itemsPorPagina)
    {
        var cut = Render<SelectorMultiple>(p => p
            .Add(c => c.Elementos, Elementos(3))
            .Add(c => c.ItemsPorPagina, itemsPorPagina));

        Listados(cut).Should().HaveCount(1);
    }
}
