using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

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
    // BarraFiltros pinta «Filtros guardados», «Limpiar todo» y «Guardar filtro» con
    // IStringLocalizer<TextosComunes>.
    public BarraFiltrosTests() => Services.AddLocalization();

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

    [Fact]
    public void Sin_pastillas_la_barra_sigue_en_su_modo_de_selects()
    {
        var cut = RenderizarConDosSelects();

        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty("Empresas, Centros, Subcontratas y Documentos no cambian en la fase 1");
        cut.FindAll("[data-filtro-pantalla]").Should().BeEmpty();
        cut.Find(".barra-filtros-lista input[type=text]").GetAttribute("placeholder").Should().Be("Buscar por nombre…");
    }

    // ------------------------------------------- Modo pastillas (rediseño de listados, fase 1)

    private IRenderedComponent<BarraFiltros> RenderizarConPastillas(Action<ComponentParameterCollectionBuilder<BarraFiltros>>? ajustes = null)
    {
        // Las pastillas son MenuAcciones: al abrirse piden el foco por JS.
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Render<BarraFiltros>(p =>
        {
            p.Add(b => b.Pastillas, (RenderFragment)(builder =>
            {
                builder.OpenComponent<PastillaFiltro>(0);
                builder.AddAttribute(1, nameof(PastillaFiltro.Etiqueta), "Estado");
                builder.AddAttribute(2, nameof(PastillaFiltro.Opciones), (IReadOnlyList<OpcionEstado>)[new OpcionEstado("Vencido", "Con vencidos")]);
                builder.CloseComponent();
            }));
            ajustes?.Invoke(p);
        });
    }

    [Fact]
    public void Con_pastillas_el_buscador_es_Filtrar_esta_pantalla_con_la_tecla_F()
    {
        var cut = RenderizarConPastillas();

        var buscador = cut.Find(".barra-filtros-pastillas input[type=text]");
        buscador.GetAttribute("placeholder").Should().Be("Filtrar esta pantalla");
        buscador.GetAttribute("aria-label").Should().Be("Filtrar esta pantalla");
        buscador.GetAttribute("aria-keyshortcuts").Should().Be("f");
        buscador.HasAttribute("data-filtro-pantalla").Should().BeTrue("es el gancho de la tecla f en atajos-lista.js");
        cut.Find(".barra-filtros-buscador-pantalla kbd.barra-filtros-tecla").TextContent.Trim().Should().Be("F");
        cut.FindAll(".barra-filtros-lista").Should().BeEmpty();
    }

    [Fact]
    public void Con_pastillas_el_placeholder_propio_manda()
    {
        var cut = RenderizarConPastillas(p => p.Add(b => b.PlaceholderBuscador, "Filtrar esta pantalla: nombre"));

        cut.Find("[data-filtro-pantalla]").GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: nombre");
    }

    [Fact]
    public void Las_pastillas_van_tras_el_buscador_y_sin_nada_que_ofrecer_no_hay_Mas_filtros()
    {
        var cut = RenderizarConPastillas(p => p.Add(b => b.Resumen, (RenderFragment)(r => r.AddContent(0, "3 con estos filtros"))));

        var fila = cut.Find(".barra-filtros-pastillas-fila");
        fila.Children.Select(e => e.ClassList[0]).Should().Equal("barra-filtros-buscador-pantalla", "menu-acciones", "barra-filtros-resumen");
        cut.FindAll(".menu-acciones-disparador").Select(d => d.GetAttribute("aria-label")).Should().Equal("Estado");
        cut.Find(".barra-filtros-resumen").TextContent.Should().Be("3 con estos filtros");
    }

    [Fact]
    public async Task Mas_filtros_aplica_y_borra_los_guardados_y_guarda_el_actual()
    {
        var aplicados = new List<string>();
        var borrados = new List<string>();
        var guardados = 0;
        var cut = RenderizarConPastillas(p => p
            .Add(b => b.HayFiltrosActivos, true)
            .Add(b => b.FiltrosGuardados, [new OpcionEstado("f-1", "Críticos de Marta")])
            .Add(b => b.OnAplicarFiltroGuardado, v => aplicados.Add(v))
            .Add(b => b.OnBorrarFiltroGuardado, v => borrados.Add(v))
            .Add(b => b.OnGuardarFiltro, () => guardados++));

        var masFiltros = () => cut.FindAll(".menu-acciones-disparador").Single(d => d.GetAttribute("aria-label") == "Más filtros");

        await masFiltros().ClickAsync(new MouseEventArgs());
        cut.Find("[role=menu] .menu-filtros-titulo").TextContent.Trim().Should().Be("Filtros guardados");
        cut.FindAll("[role=menu] [role=menuitem]").Select(i => i.GetAttribute("aria-label") ?? i.TextContent.Trim())
            .Should().Equal("Críticos de Marta", "Borrar filtro guardado Críticos de Marta", "Guardar filtro");

        await cut.FindAll("[role=menu] [role=menuitem]")[0].ClickAsync(new MouseEventArgs());
        aplicados.Should().Equal("f-1");

        await masFiltros().ClickAsync(new MouseEventArgs());
        await cut.Find("[aria-label='Borrar filtro guardado Críticos de Marta']").ClickAsync(new MouseEventArgs());
        borrados.Should().Equal("f-1");
        aplicados.Should().HaveCount(1, "el aspa borra; no aplica");

        await masFiltros().ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=menu] [role=menuitem]").Single(i => i.TextContent.Trim() == "Guardar filtro").ClickAsync(new MouseEventArgs());
        guardados.Should().Be(1);
    }

    [Fact]
    public async Task Sin_filtros_activos_Guardar_filtro_se_ve_deshabilitado_y_no_hay_chips()
    {
        var guardados = 0;
        var cut = RenderizarConPastillas(p => p
            .Add(b => b.HayFiltrosActivos, false)
            .Add(b => b.OnGuardarFiltro, () => guardados++));

        cut.FindAll(".chips-filtros").Should().BeEmpty();
        cut.FindAll(".limpiar-filtros-barra").Should().BeEmpty();

        await cut.FindAll(".menu-acciones-disparador").Single(d => d.GetAttribute("aria-label") == "Más filtros").ClickAsync(new MouseEventArgs());
        var guardar = cut.FindAll("[role=menu] [role=menuitem]").Single(i => i.TextContent.Trim() == "Guardar filtro");
        guardar.HasAttribute("disabled").Should().BeTrue();
        await guardar.ClickAsync(new MouseEventArgs());
        guardados.Should().Be(0);
    }

    [Fact]
    public async Task Con_filtros_activos_los_chips_y_Limpiar_todo_van_debajo_de_las_pastillas()
    {
        var limpiezas = 0;
        var cut = RenderizarConPastillas(p => p
            .Add(b => b.HayFiltrosActivos, true)
            .Add(b => b.OnLimpiarTodo, () => limpiezas++)
            .Add(b => b.Chips, (RenderFragment)(c =>
            {
                c.OpenComponent<ChipFiltro>(0);
                c.AddAttribute(1, nameof(ChipFiltro.Etiqueta), "Estado: Vencido");
                c.CloseComponent();
            })));

        var chips = cut.Find(".barra-filtros-pastillas > .chips-filtros");
        chips.TextContent.Should().Contain("Estado: Vencido");
        await chips.QuerySelector("button.limpiar-filtros-barra")!.ClickAsync(new MouseEventArgs());
        limpiezas.Should().Be(1);
    }
}
