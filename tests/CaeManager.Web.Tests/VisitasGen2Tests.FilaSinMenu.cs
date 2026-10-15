using Bunit;
using CaeManager.Application.Visitas.Commands.CancelarVisitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados sin menú «⋯» en Visitas (adopción G, 2026-10-09): la fila se marca pulsable y
/// sus fechas son el botón que abre la vista rápida; icono 360 al final de la fila; lápiz en la
/// cabecera del panel y tecla «e»; cancelar solo desde la selección múltiple, que «x» enciende.
/// El clic en un punto de la fila sin control lo atiende atajos-lista.js y solo lo prueba el E2E
/// (<c>VisitasFilaSinMenuE2ETests</c>).
/// </summary>
public partial class VisitasGen2Tests
{
    private const string SelectorLapiz = ".visitas-detalle-titulo button[aria-label='Editar la visita']";

    private static bool FormularioDeEdicionAbierto(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".drawer-header h2").Any(h => h.TextContent.Trim() == "Editar visita");

    private static bool PanelAbierto(IRenderedComponent<Visitas> cut) =>
        cut.FindAll(".drawer-header h2").Any(h => h.TextContent.Trim() == "Detalle de la visita");

    private async Task<IRenderedComponent<Visitas>> ConUnaVisitaAsync(string rol, bool cancelada = false)
    {
        this.ConRolDeEscritura(rol);
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Centro Norte") with { EstaCancelada = cancelada });
        var cut = Renderizar(mediator);
        if (cancelada)
        {
            await FiltrosVisitasDePrueba.ElegirAsync(cut, "Solo activas", "No");
        }

        cut.WaitForAssertion(() => Fila(cut, "Centro Norte"));
        return cut;
    }

    // ------------------------------------------------------------------ la fila

    [Fact]
    public void La_fila_no_lleva_menu_se_marca_pulsable_y_sus_fechas_son_el_boton_de_la_vista_rapida()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        var fila = Fila(cut, "Centro Norte");
        fila.ClassList.Should().Contain("fila-pulsable", "es la marca que lee el oyente de clic de fila");
        fila.QuerySelectorAll(".menu-acciones-disparador").Should().BeEmpty();

        var boton = BotonVistaRapida(cut, "Centro Norte");
        boton.TextContent.Trim().Should().Be(TextoFechasEsperado(norte));
        boton.GetAttribute("aria-label").Should().Be($"Abrir la vista rápida de la visita a Centro Norte, {TextoFechasEsperado(norte)}");
        fila.QuerySelectorAll("button.nombre-abre-vista-rapida").Should().ContainSingle("el oyente pulsa el primero que encuentra");
    }

    [Fact]
    public async Task El_boton_de_las_fechas_abre_la_vista_rapida_de_esa_visita()
    {
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([Visita("Centro Norte"), Visita("Planta Zaragoza")]);
        var cut = Renderizar(mediator);

        await BotonVistaRapida(cut, "Planta Zaragoza").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".visitas-detalle-centro").TextContent.Trim().Should().Be("Planta Zaragoza"));
    }

    [Fact]
    public async Task Enter_sobre_la_fila_enfocada_abre_su_vista_rapida()
    {
        var cut = await ConUnaVisitaAsync(Roles.GestorCae);

        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "Enter");

        cut.WaitForAssertion(() => cut.Find(".visitas-detalle-centro").TextContent.Trim().Should().Be("Centro Norte"));
    }

    [Fact]
    public void El_icono_360_de_la_fila_es_un_enlace_a_la_pagina_de_la_visita()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);

        var enlace = Fila(cut, "Centro Norte").QuerySelector("a.boton-360-pagina");

        enlace.Should().NotBeNull();
        enlace!.GetAttribute("href").Should().Be($"/visitas/{norte.Id}");
        enlace.GetAttribute("aria-label").Should().Contain("la visita a Centro Norte", "con varias filas, el nombre accesible dice cuál abre");
    }

    // ------------------------------------------------------------------ lápiz de la cabecera del panel

    [Fact]
    public async Task El_lapiz_esta_en_la_cabecera_del_panel_en_las_tres_pestanas()
    {
        var cut = await ConUnaVisitaAsync(Roles.GestorCae);
        await BotonVistaRapida(cut, "Centro Norte").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel [role=tab]").Should().HaveCount(3));

        foreach (var indice in Enumerable.Range(0, 3))
        {
            await cut.FindAll(".drawer-panel [role=tab]")[indice].ClickAsync(new MouseEventArgs());
            cut.FindAll(SelectorLapiz).Should().ContainSingle($"el lápiz no depende de la pestaña (pestaña {indice})");
        }
    }

    [Fact]
    public async Task El_lapiz_cierra_el_panel_y_abre_el_formulario_de_edicion_de_esa_visita()
    {
        var cut = await ConUnaVisitaAsync(Roles.GestorCae);
        await BotonVistaRapida(cut, "Centro Norte").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find(SelectorLapiz));

        await cut.Find(SelectorLapiz).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => FormularioDeEdicionAbierto(cut).Should().BeTrue());
        PanelAbierto(cut).Should().BeFalse("no quedan dos paneles apilados");
        cut.Find(".drawer-panel").TextContent.Should().Contain("Centro Norte");
    }

    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public async Task El_lapiz_no_se_ofrece_a_Consulta_ni_en_una_visita_cancelada(string rol, bool cancelada)
    {
        // EditarVisitaCommand es un ICommand denegado a Consulta, y una Visita cancelada no se edita: se reactiva.
        var cut = await ConUnaVisitaAsync(rol, cancelada);
        await BotonVistaRapida(cut, "Centro Norte").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".visitas-detalle-titulo .visitas-detalle-centro").TextContent.Trim().Should().Be("Centro Norte",
            "control positivo: la cabecera del panel se pintó"));
        cut.FindAll(SelectorLapiz).Should().BeEmpty();
    }

    // ------------------------------------------------------------------ tecla «e»

    [Fact]
    public async Task La_tecla_e_abre_el_formulario_de_edicion_de_la_fila_enfocada()
    {
        var mediator = new MediatorVisitas();
        mediator.Visitas.AddRange([Visita("Centro Norte"), Visita("Planta Zaragoza")]);
        var cut = Renderizar(mediator);

        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "j");
        cut.WaitForAssertion(() => Fila(cut, "Planta Zaragoza").ClassList.Should().Contain("fila-enfocada"));
        await AtajoAsync(cut, "e");

        cut.WaitForAssertion(() => FormularioDeEdicionAbierto(cut).Should().BeTrue());
        cut.Find(".drawer-panel").TextContent.Should().Contain("Planta Zaragoza");
    }

    [Fact]
    public async Task La_tecla_e_sin_fila_enfocada_no_abre_nada()
    {
        var cut = await ConUnaVisitaAsync(Roles.GestorCae);

        await AtajoAsync(cut, "e");

        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task La_tecla_e_con_el_panel_abierto_no_apila_el_formulario_encima()
    {
        // En el navegador el panel es modal y la tecla no llega; la página no depende de eso.
        var cut = await ConUnaVisitaAsync(Roles.GestorCae);
        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "Enter");
        cut.WaitForAssertion(() => PanelAbierto(cut).Should().BeTrue("control positivo: el panel está abierto"));

        await AtajoAsync(cut, "e");

        FormularioDeEdicionAbierto(cut).Should().BeFalse();
        PanelAbierto(cut).Should().BeTrue();
    }

    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public async Task La_tecla_e_no_edita_para_Consulta_ni_una_visita_cancelada(string rol, bool cancelada)
    {
        var cut = await ConUnaVisitaAsync(rol, cancelada);

        await AtajoAsync(cut, "j");
        cut.WaitForAssertion(() => Fila(cut, "Centro Norte").ClassList.Should().Contain("fila-enfocada", "control positivo: hay fila enfocada"));
        await AtajoAsync(cut, "e");

        cut.FindAll(".drawer-panel").Should().BeEmpty();
    }

    // ------------------------------------------------------------------ selección múltiple

    [Fact]
    public async Task La_tecla_x_enciende_la_seleccion_multiple_y_marca_la_fila_enfocada()
    {
        var norte = Visita("Centro Norte");
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(norte);
        var cut = Renderizar(mediator);
        Fila(cut, "Centro Norte").QuerySelectorAll("input[type=checkbox]:not(.visitas-interruptor)").Should().BeEmpty("la selección múltiple empieza apagada");

        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "x");

        cut.WaitForAssertion(() => Fila(cut, "Centro Norte").QuerySelector("input[type=checkbox]:not(.visitas-interruptor)")!
            .HasAttribute("checked").Should().BeTrue("la casilla de la fila marcada queda a la vista"));

        await cut.FindAll(".barra-acciones-lote button").First(b => b.TextContent.Trim() == "Cancelar seleccionadas").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar seleccionadas").ClickAsync(new MouseEventArgs());

        mediator.Comandos.OfType<CancelarVisitasCommand>().Should().ContainSingle().Which.Ids.Should().Equal(norte.Id);
    }

    /// <summary>La cancelación individual, con su diálogo, ya no existe en la lista: solo quedan el del lote y el de reactivar.</summary>
    [Fact]
    public void La_lista_no_monta_el_dialogo_de_la_cancelacion_individual()
    {
        var mediator = new MediatorVisitas();
        mediator.Visitas.Add(Visita("Centro Norte"));
        var cut = Renderizar(mediator);

        // Cuatro: quitar un Trabajador, reactivar, cancelar en lote y borrar un filtro guardado (la pieza
        // FiltrosGuardadosDeListado monta el suyo).
        cut.FindComponents<DialogoConfirmacion>().Should().HaveCount(4);
    }

    // Mismo formato que Visitas.TextoFechas, que es privado en la página.
    private static string TextoFechasEsperado(CaeManager.Application.Visitas.Queries.ObtenerVisitas.VisitaListaDto visita) =>
        visita.FechaInicio == visita.FechaFin
            ? visita.FechaInicio.ToString("dd/MM/yyyy")
            : $"{visita.FechaInicio:dd/MM/yyyy} – {visita.FechaFin:dd/MM/yyyy}";
}
