using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pieza 2 del patrón único de pantalla de lista
/// (Project-Hydra-Negocio/tecnico/CONTRATO-PATRON-PANTALLA-LISTA-2026-09-28.md § 3):
/// tarjeta con buscador y selects con etiqueta VISIBLE encima, «Filtros
/// guardados» al final y, debajo, chips con ✕, «Limpiar todo» y «Guardar
/// filtro». Que «Limpiar todo» limpie también la URL es cosa de quien recibe
/// el evento y lo prueba cada lista (Clientes:
/// <c>Limpiar_todo_quita_los_filtros_de_la_consulta_y_de_la_url</c>).
/// </summary>
public class BarraFiltrosTests : BunitContext
{
    private IRenderedComponent<BarraFiltros> RenderizarConDosSelects(Action<ComponentParameterCollectionBuilder<BarraFiltros>>? ajustes = null) =>
        Render<BarraFiltros>(p =>
        {
            p.Add(b => b.PlaceholderBuscador, "Buscar por nombre…");
            p.Add(b => b.Filtros, (RenderFragment)(builder =>
            {
                builder.OpenComponent<CampoSelect>(0);
                builder.AddAttribute(1, nameof(CampoSelect.Etiqueta), "Gestor CAE");
                builder.AddAttribute(2, nameof(CampoSelect.ChildContent), (RenderFragment)(o => o.AddMarkupContent(0, "<option value=\"\">Todos</option>")));
                builder.CloseComponent();
                builder.OpenComponent<CampoSelect>(3);
                builder.AddAttribute(4, nameof(CampoSelect.Etiqueta), "Estado");
                builder.AddAttribute(5, nameof(CampoSelect.ChildContent), (RenderFragment)(o => o.AddMarkupContent(0, "<option value=\"\">Todos</option>")));
                builder.CloseComponent();
            }));
            ajustes?.Invoke(p);
        });

    [Fact]
    public void Cada_control_lleva_su_etiqueta_visible_asociada_al_campo()
    {
        var cut = RenderizarConDosSelects();

        var etiquetas = cut.FindAll(".barra-filtros-lista label");
        etiquetas.Select(l => l.TextContent.Trim()).Should().Equal("Buscar", "Gestor CAE", "Estado");
        foreach (var etiqueta in etiquetas)
        {
            var id = etiqueta.GetAttribute("for");
            id.Should().NotBeNullOrEmpty();
            cut.FindAll("#" + id).Should().ContainSingle($"la etiqueta «{etiqueta.TextContent.Trim()}» apunta a un control real");
        }
    }

    [Fact]
    public void El_texto_de_las_opciones_no_repite_el_nombre_del_filtro()
    {
        var cut = RenderizarConDosSelects();

        cut.FindAll(".barra-filtros-lista option").Select(o => o.TextContent.Trim())
            .Should().OnlyContain(t => t == "Todos", "la etiqueta va encima, no dentro de la opción («Estado: todos»)");
    }

    [Fact]
    public void Sin_filtros_activos_no_pinta_chips_ni_limpiar_ni_guardar()
    {
        var cut = RenderizarConDosSelects(p => p
            .Add(b => b.HayFiltrosActivos, false)
            .Add(b => b.OnGuardarFiltro, _ => { }));

        cut.FindAll(".chip-filtro").Should().BeEmpty();
        cut.FindAll(".limpiar-filtros-barra").Should().BeEmpty();
        cut.FindAll(".guardar-filtro-barra").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_filtros_activos_pinta_los_chips_y_el_de_quitar_avisa_de_cual()
    {
        var quitados = new List<string>();
        var cut = RenderizarConDosSelects(p => p
            .Add(b => b.HayFiltrosActivos, true)
            .Add(b => b.Chips, (RenderFragment)(builder =>
            {
                foreach (var (texto, nombre) in new[] { ("Estado: Con vencidos", "Estado"), ("Gestor CAE: Marta", "Gestor CAE") })
                {
                    builder.OpenComponent<ChipFiltro>(0);
                    builder.AddAttribute(1, nameof(ChipFiltro.Etiqueta), texto);
                    builder.AddAttribute(2, nameof(ChipFiltro.EtiquetaAccesible), $"Quitar filtro de {nombre}");
                    builder.AddAttribute(3, nameof(ChipFiltro.OnQuitar), EventCallback.Factory.Create(this, () => quitados.Add(nombre)));
                    builder.CloseComponent();
                }
            })));

        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Estado: Con vencidos", "Gestor CAE: Marta");

        await cut.Find("button[aria-label='Quitar filtro de Gestor CAE']").ClickAsync(new MouseEventArgs());

        quitados.Should().Equal("Gestor CAE");
    }

    [Fact]
    public async Task Limpiar_todo_va_junto_a_los_chips_e_invoca_el_evento()
    {
        var limpiado = 0;
        var cut = RenderizarConDosSelects(p => p
            .Add(b => b.HayFiltrosActivos, true)
            .Add(b => b.OnLimpiarTodo, () => limpiado++));

        var boton = cut.Find(".barra-filtros-lista-chips .limpiar-filtros-barra");
        boton.TextContent.Trim().Should().Be("Limpiar todo");
        await boton.ClickAsync(new MouseEventArgs());

        limpiado.Should().Be(1);
    }

    [Fact]
    public async Task Guardar_filtro_va_a_la_derecha_de_los_chips_e_invoca_el_evento()
    {
        var guardado = 0;
        var cut = RenderizarConDosSelects(p => p
            .Add(b => b.HayFiltrosActivos, true)
            .Add(b => b.OnGuardarFiltro, () => guardado++));

        cut.Find(".barra-filtros-lista-chips").QuerySelector(".guardar-filtro-barra").Should().BeNull("no va dentro de los chips");
        var boton = cut.Find(".barra-filtros-lista-activos > .guardar-filtro-barra");
        boton.TextContent.Trim().Should().Be("Guardar filtro");
        await boton.ClickAsync(new MouseEventArgs());

        guardado.Should().Be(1);
    }

    [Fact]
    public void Sin_delegado_de_guardar_no_ofrece_guardar_filtro()
    {
        var cut = RenderizarConDosSelects(p => p.Add(b => b.HayFiltrosActivos, true));

        cut.FindAll(".guardar-filtro-barra").Should().BeEmpty();
    }

    [Fact]
    public async Task Filtros_guardados_va_al_final_de_la_fila_con_su_etiqueta_y_avisa_del_elegido()
    {
        string? elegido = null;
        var cut = RenderizarConDosSelects(p => p
            .Add(b => b.FiltrosGuardados, [new OpcionEstado("f-1", "Críticos de Marta")])
            .Add(b => b.OnAplicarFiltroGuardado, v => elegido = v));

        cut.FindAll(".barra-filtros-lista label").Select(l => l.TextContent.Trim())
            .Should().Equal("Buscar", "Gestor CAE", "Estado", "Filtros guardados");

        var etiqueta = cut.FindAll(".barra-filtros-lista label").Last();
        await cut.Find("select#" + etiqueta.GetAttribute("for")).ChangeAsync(new ChangeEventArgs { Value = "f-1" });

        elegido.Should().Be("f-1");
    }

    [Fact]
    public void Sin_filtros_guardados_no_pinta_su_select()
    {
        var cut = RenderizarConDosSelects();

        cut.FindAll(".barra-filtros-lista label").Select(l => l.TextContent.Trim()).Should().NotContain("Filtros guardados");
    }

    [Fact]
    public void La_barra_no_lleva_acciones_de_la_pagina()
    {
        var cut = RenderizarConDosSelects(p => p.Add(b => b.HayFiltrosActivos, false));

        cut.FindAll(".barra-filtros-lista button").Should().BeEmpty("Exportar, Importar y Nuevo son de la cabecera, no de la barra de filtros");
    }
}
