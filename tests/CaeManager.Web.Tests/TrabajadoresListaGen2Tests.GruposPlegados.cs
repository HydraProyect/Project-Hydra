using AngleSharp.Dom;
using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Agrupada, la pantalla carga TODAS las filas filtradas pero solo pinta las de los grupos abiertos: «seleccionar todo»
/// (que alimenta «Asignar a un centro» y «Eliminar seleccionados») y los atajos no pueden alcanzar filas que no se ven.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private const string EmpresaGrupoEbro = "Montajes Ebro S.L.";
    private const string EmpresaGrupoDexter = "Dexter Industrial S.A.";

    /// <summary>Ana y Carla son de Montajes Ebro y Bea de Dexter; agrupada «Por Empresa» y con todo plegado.</summary>
    private IRenderedComponent<Trabajadores> RenderizarAgrupadaPlegada()
    {
        var cut = Renderizar(ConTresEmpresas(), "trabajadores?agrupar=empresa");
        cut.FindAll("button.grupo-lista-cabecera").Should().HaveCount(2);
        FilasDeDatos(cut).Should().BeEmpty("control positivo: todo nace plegado");
        return cut;
    }

    private static async Task AlternarGrupoDeEmpresaAsync(IRenderedComponent<Trabajadores> cut, string empresa) =>
        await cut.FindAll("[data-grupo]").Single(g => g.GetAttribute("data-grupo") == empresa)
            .QuerySelector("button.grupo-lista-cabecera")!.ClickAsync(new MouseEventArgs());

    private static async Task SeleccionarTodoAsync(IRenderedComponent<Trabajadores> cut)
    {
        await AlternarSeleccionMultiple(cut);
        await cut.Find("input[aria-label='Seleccionar los trabajadores de esta página']")
            .ChangeAsync(new ChangeEventArgs { Value = true });
    }

    /// <summary>Cuántos Trabajadores dice la barra de acciones por lote; 0 si la barra no aparece.</summary>
    private static int Seleccionados(IRenderedComponent<Trabajadores> cut)
    {
        var cantidad = cut.FindAll(".barra-acciones-lote-cantidad").FirstOrDefault();
        return cantidad is null ? 0 : int.Parse(new string(cantidad.TextContent.TakeWhile(char.IsDigit).ToArray()));
    }

    [Fact]
    public async Task Agrupada_y_plegada_seleccionar_todo_no_selecciona_ningun_trabajador_oculto()
    {
        var cut = RenderizarAgrupadaPlegada();

        await SeleccionarTodoAsync(cut);

        Seleccionados(cut).Should().Be(0, "los Trabajadores de un grupo plegado no se ven y la barra de lote no puede actuar sobre ellos");
        cut.FindAll(".barra-acciones-lote button").Should().NotContain(b => b.TextContent.Trim() == "Asignar a un centro");
    }

    [Fact]
    public async Task Tras_abrir_un_grupo_seleccionar_todo_selecciona_solo_los_trabajadores_de_ese_grupo()
    {
        var cut = RenderizarAgrupadaPlegada();
        await AlternarGrupoDeEmpresaAsync(cut, EmpresaGrupoEbro);
        FilasDeDatos(cut).Should().HaveCount(2, "control positivo: solo se pintan los del grupo abierto");

        await SeleccionarTodoAsync(cut);

        Seleccionados(cut).Should().Be(2, "Ana y Carla; Bea, de Dexter, sigue plegada y sin seleccionar");
    }

    [Fact]
    public async Task Plegar_un_grupo_con_trabajadores_seleccionados_los_deselecciona()
    {
        var cut = RenderizarAgrupadaPlegada();
        await AlternarGrupoDeEmpresaAsync(cut, EmpresaGrupoEbro);
        await AlternarGrupoDeEmpresaAsync(cut, EmpresaGrupoDexter);
        await SeleccionarTodoAsync(cut);
        Seleccionados(cut).Should().Be(3, "control positivo: con los dos grupos abiertos se seleccionan los tres");

        await AlternarGrupoDeEmpresaAsync(cut, EmpresaGrupoEbro);

        Seleccionados(cut).Should().Be(1, "Ana y Carla dejan de estar seleccionadas al plegar su grupo; Bea sigue");
    }

    [Fact]
    public async Task Agrupada_y_plegada_los_atajos_j_y_x_no_enfocan_ni_marcan_trabajadores_ocultos()
    {
        var cut = RenderizarAgrupadaPlegada();
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("x"));

        cut.FindAll(".fila-enfocada").Should().BeEmpty();
        Seleccionados(cut).Should().Be(0);

        await AlternarGrupoDeEmpresaAsync(cut, EmpresaGrupoEbro);
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        cut.FindAll(".fila-enfocada").Should().ContainSingle("control positivo: con el grupo abierto la j sí enfoca");

        await AlternarGrupoDeEmpresaAsync(cut, EmpresaGrupoEbro);
        cut.FindAll(".fila-enfocada").Should().BeEmpty("al plegar el grupo la fila enfocada deja de estar enfocada");
    }
}
