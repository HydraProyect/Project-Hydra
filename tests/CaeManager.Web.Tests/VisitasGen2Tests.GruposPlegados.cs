using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Agrupada, la pantalla carga TODAS las filas filtradas pero solo pinta las de los grupos abiertos: «seleccionar todo»,
/// los atajos y «Cancelar seleccionadas» no pueden alcanzar filas que no se ven.
/// </summary>
public partial class VisitasGen2Tests
{
    private const string ClienteIberojet = "Iberojet S.A.";
    private const string ClienteOtro = "Otro Cliente S.A.";

    /// <summary>Dos Visitas del mismo Cliente (Iberojet) y una de otro, con la lista agrupada «Por Cliente» y todo plegado.</summary>
    private IRenderedComponent<Visitas> RenderizarAgrupadaPlegada()
    {
        var a1 = Visita("Centro A1");
        var a2 = Visita("Centro A2") with { ClienteId = a1.ClienteId, ClienteRazonSocial = a1.ClienteRazonSocial };
        var b = Visita("Centro B") with { ClienteRazonSocial = ClienteOtro };
        var cut = Renderizar(new MediatorVisitas { Visitas = { a1, a2, b } }, "visitas?agrupar=cliente");
        cut.WaitForAssertion(() => cut.FindAll("button.grupo-lista-cabecera").Should().HaveCount(2));
        return cut;
    }

    private static async Task AlternarGrupoDeClienteAsync(IRenderedComponent<Visitas> cut, string cliente) =>
        await cut.FindAll(".visitas-grupo").Single(g => g.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim() == cliente)
            .QuerySelector("button.grupo-lista-cabecera")!.ClickAsync(new MouseEventArgs());

    private static async Task SeleccionarTodoAsync(IRenderedComponent<Visitas> cut)
    {
        await cut.FindAll("button").First(b => b.GetAttribute("aria-label") == "Selección múltiple").ClickAsync(new MouseEventArgs());
        await cut.Find("thead input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });
    }

    /// <summary>Cuántas Visitas dice la barra de acciones por lote; 0 si la barra no aparece.</summary>
    private static int Seleccionadas(IRenderedComponent<Visitas> cut)
    {
        var cantidad = cut.FindAll(".barra-acciones-lote-cantidad").FirstOrDefault();
        return cantidad is null ? 0 : int.Parse(new string(cantidad.TextContent.TakeWhile(char.IsDigit).ToArray()));
    }

    [Fact]
    public async Task Agrupada_y_plegada_seleccionar_todo_no_selecciona_ninguna_visita_oculta()
    {
        var cut = RenderizarAgrupadaPlegada();
        cut.FindAll("tr[data-id]").Should().BeEmpty("control positivo: todo nace plegado");

        await SeleccionarTodoAsync(cut);

        Seleccionadas(cut).Should().Be(0, "las Visitas de un grupo plegado no se ven y «Cancelar seleccionadas» no puede llegar a ellas");
        cut.FindAll(".barra-acciones-lote button").Should().NotContain(b => b.TextContent.Trim() == "Cancelar seleccionadas",
            "sin filas pintadas no hay selección que cancelar");
    }

    [Fact]
    public async Task Tras_abrir_un_grupo_seleccionar_todo_selecciona_solo_las_visitas_de_ese_grupo()
    {
        var cut = RenderizarAgrupadaPlegada();
        await AlternarGrupoDeClienteAsync(cut, ClienteIberojet);
        cut.FindAll("tr[data-id]").Should().HaveCount(2, "control positivo: solo se pintan las del grupo abierto");

        await SeleccionarTodoAsync(cut);

        Seleccionadas(cut).Should().Be(2, "las dos Visitas de Iberojet; la del Otro Cliente sigue plegada y sin seleccionar");
        cut.FindAll("tbody input[type=checkbox]:not(.visitas-interruptor):checked").Should().HaveCount(2);
    }

    [Fact]
    public async Task Plegar_un_grupo_con_visitas_seleccionadas_las_deselecciona()
    {
        var cut = RenderizarAgrupadaPlegada();
        await AlternarGrupoDeClienteAsync(cut, ClienteIberojet);
        await AlternarGrupoDeClienteAsync(cut, ClienteOtro);
        await SeleccionarTodoAsync(cut);
        Seleccionadas(cut).Should().Be(3, "control positivo: con los dos grupos abiertos se seleccionan las tres");

        await AlternarGrupoDeClienteAsync(cut, ClienteIberojet);

        Seleccionadas(cut).Should().Be(1, "las dos Visitas del grupo que se pliega dejan de estar seleccionadas");
    }

    [Fact]
    public async Task Agrupada_y_plegada_los_atajos_j_y_x_no_enfocan_ni_marcan_visitas_ocultas()
    {
        var cut = RenderizarAgrupadaPlegada();

        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "x");

        cut.FindAll(".fila-enfocada").Should().BeEmpty();
        Seleccionadas(cut).Should().Be(0);

        await AlternarGrupoDeClienteAsync(cut, ClienteIberojet);
        await AtajoAsync(cut, "j");
        cut.FindAll(".fila-enfocada").Should().ContainSingle("control positivo: con el grupo abierto la j sí enfoca");

        await AlternarGrupoDeClienteAsync(cut, ClienteIberojet);
        cut.FindAll(".fila-enfocada").Should().BeEmpty("al plegar el grupo la fila enfocada deja de estar enfocada");
    }
}
