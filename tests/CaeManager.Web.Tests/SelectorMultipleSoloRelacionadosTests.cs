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
}
