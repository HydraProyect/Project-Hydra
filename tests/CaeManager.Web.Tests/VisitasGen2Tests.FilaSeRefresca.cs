using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Visitas.Commands.EditarVisita;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Guardar la edición de una Visita sustituye su fila en sitio: la página vuelve a pedir SOLO esa
/// fila, con el filtro por id de la consulta de lista, y QuickGrid la repinta sin una carga de
/// página (que volvería a la página 1 y limpiaría la selección y la fila enfocada). Aquí se
/// recorre entero: tecla «e» sobre la fila enfocada, formulario y «Guardar».
/// </summary>
public partial class VisitasGen2Tests
{
    private static MediatorVisitas MediadorQueEdita(Func<VisitaListaDto, VisitaListaDto> cambio, params VisitaListaDto[] visitas)
    {
        var mediator = new MediatorVisitas
        {
            TrabajadoresAlEditar = [Guid.NewGuid()],
            AlEditar = (visita, _) => cambio(visita) with { Version = Guid.NewGuid() },
        };
        mediator.Visitas.AddRange(visitas);
        return mediator;
    }

    private static MediatorVisitas ConVeinticincoQueEdita(Func<VisitaListaDto, VisitaListaDto> cambio) =>
        MediadorQueEdita(cambio, Enumerable.Range(1, 25).Select(i => Visita($"Centro {i:00}")).ToArray());

    private static VisitaListaDto UnaSemanaMas(VisitaListaDto visita) => visita with { FechaFin = visita.FechaFin.AddDays(7) };

    private static int ConsultasDePagina(MediatorVisitas mediator) => mediator.ConsultasDeLista.Count(c => c.VisitaId is null);

    /// <summary>El Centro de cada fila: el primer nombre de la columna «Visita» (debajo van el titular y la Empresa).</summary>
    private static List<string> CentrosDeLasFilas(IRenderedComponent<Visitas> cut) =>
        cut.FindAll("tbody tr.fila-pulsable")
            .Select(tr => tr.QuerySelector(".visitas-celda-apilada .enlace-nombre-fila")!.TextContent.Trim())
            .ToList();

    private static IElement CasillaDeSeleccion(IRenderedComponent<Visitas> cut, string centro) =>
        Fila(cut, centro).QuerySelector("input[type=checkbox]:not(.visitas-interruptor)")
            ?? throw new InvalidOperationException($"La fila de {centro} no tiene casilla de selección.");

    /// <summary>«e» sobre la fila enfocada, formulario de edición y «Guardar»; vuelve con el formulario cerrado.</summary>
    private static async Task EditarLaFilaEnfocadaYGuardarAsync(IRenderedComponent<Visitas> cut)
    {
        await AtajoAsync(cut, "e");
        cut.WaitForAssertion(() => FormularioDeEdicionAbierto(cut).Should().BeTrue("barrera: el formulario de edición se abrió"));
        await cut.FindAll(".drawer-panel button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => FormularioDeEdicionAbierto(cut).Should().BeFalse("el guardado terminó y cerró el formulario"));
    }

    private static async Task IrALaPaginaDosDeVisitasAsync(IRenderedComponent<Visitas> cut)
    {
        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => CentrosDeLasFilas(cut).Should().HaveCount(5));
    }

    [Fact]
    public async Task Guardar_la_edicion_sustituye_la_fila_en_sitio_con_una_sola_consulta_por_id()
    {
        var norte = Visita("Centro Norte");
        var mediator = MediadorQueEdita(UnaSemanaMas, norte, Visita("Planta Zaragoza"));
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => BotonVistaRapida(cut, "Centro Norte").TextContent.Trim().Should().Be(TextoFechasEsperado(norte), "punto de partida"));
        await AtajoAsync(cut, "j");
        var (listaAntes, paginaAntes) = (mediator.ConsultasDeLista.Count, ConsultasDePagina(mediator));

        await EditarLaFilaEnfocadaYGuardarAsync(cut);

        mediator.Comandos.OfType<EditarVisitaCommand>().Should().ContainSingle("control positivo: la edición se envió").Which.Id.Should().Be(norte.Id);
        cut.WaitForAssertion(() => BotonVistaRapida(cut, "Centro Norte").TextContent.Trim().Should().Be(TextoFechasEsperado(UnaSemanaMas(norte)),
            "la fila enseña las fechas guardadas"));
        var ultima = mediator.ConsultasDeLista[^1];
        ultima.VisitaId.Should().Be(norte.Id, "se pide solo esa fila");
        ultima.Busqueda.Should().BeNull("sin los filtros de la página: la fila se pide por su id");
        ultima.SoloActivas.Should().BeFalse();
        ultima.NotificadoCliente.Should().BeNull();
        ultima.SoloUrgentes.Should().BeFalse();
        mediator.ConsultasDeLista.Should().HaveCount(listaAntes + 1, "la consulta por id y ninguna de página detrás");
        ConsultasDePagina(mediator).Should().Be(paginaAntes, "ninguna consulta de página nueva");
    }

    [Fact]
    public async Task Guardar_la_edicion_conserva_la_pagina_la_seleccion_multiple_y_la_fila_enfocada()
    {
        var mediator = ConVeinticincoQueEdita(UnaSemanaMas);
        var editada = mediator.Visitas.Single(v => v.CentroNombre == "Centro 23");
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => CentrosDeLasFilas(cut).Should().HaveCount(20));
        await IrALaPaginaDosDeVisitasAsync(cut);
        await cut.Find("button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await CasillaDeSeleccion(cut, "Centro 22").ChangeAsync(new ChangeEventArgs { Value = true });
        await CasillaDeSeleccion(cut, "Centro 23").ChangeAsync(new ChangeEventArgs { Value = true });
        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "j");
        await AtajoAsync(cut, "j");
        cut.WaitForAssertion(() => Fila(cut, "Centro 23").ClassList.Should().Contain("fila-enfocada", "punto de partida: la tercera fila está enfocada"));
        var (listaAntes, paginaAntes) = (mediator.ConsultasDeLista.Count, ConsultasDePagina(mediator));

        await EditarLaFilaEnfocadaYGuardarAsync(cut);

        cut.WaitForAssertion(() => BotonVistaRapida(cut, "Centro 23").TextContent.Trim().Should().Be(TextoFechasEsperado(UnaSemanaMas(editada))));
        CentrosDeLasFilas(cut).Should().Equal("Centro 21", "Centro 22", "Centro 23", "Centro 24", "Centro 25");
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 visita(s)");
        CasillaDeSeleccion(cut, "Centro 22").HasAttribute("checked").Should().BeTrue("la selección de otra fila sigue marcada");
        CasillaDeSeleccion(cut, "Centro 23").HasAttribute("checked").Should().BeTrue("la fila sustituida sigue seleccionada");
        cut.FindAll("tbody input[type=checkbox]:not(.visitas-interruptor)").Count(c => c.HasAttribute("checked")).Should().Be(2);
        cut.FindAll("tbody tr.fila-pulsable").Select(tr => tr.ClassList.Contains("fila-enfocada")).Should().Equal(false, false, true, false, false);
        mediator.ConsultasDeLista.Should().HaveCount(listaAntes + 1, "una carga de página habría vuelto a la 1 y limpiado selección y foco");
        ConsultasDePagina(mediator).Should().Be(paginaAntes);
        mediator.ConsultasDeLista[^1].VisitaId.Should().Be(editada.Id);
    }

    /// <summary>
    /// «Solo activas» viene marcado de fábrica y la edición manda la Visita al pasado: la fila ya
    /// no cumple el filtro, pero permanece con sus fechas nuevas hasta la siguiente carga.
    /// </summary>
    [Fact]
    public async Task La_fila_permanece_aunque_la_edicion_la_saque_del_filtro_activo()
    {
        static VisitaListaDto AlPasado(VisitaListaDto v) => v with { FechaInicio = Hoy.AddDays(-9), FechaFin = Hoy.AddDays(-8) };
        var norte = Visita("Centro Norte");
        var mediator = MediadorQueEdita(AlPasado, norte, Visita("Planta Zaragoza"));
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => CentrosDeLasFilas(cut).Should().Equal("Centro Norte", "Planta Zaragoza"));
        await AtajoAsync(cut, "j");

        await EditarLaFilaEnfocadaYGuardarAsync(cut);

        cut.WaitForAssertion(() => BotonVistaRapida(cut, "Centro Norte").TextContent.Trim().Should().Be(TextoFechasEsperado(AlPasado(norte))));
        CentrosDeLasFilas(cut).Should().Equal("Centro Norte", "Planta Zaragoza");
        mediator.ConsultasDeLista.Last(c => c.VisitaId is null).SoloActivas.Should().BeTrue("la última consulta de página sigue siendo la del filtro");
        mediator.Visitas.Where(v => v.FechaFin >= Hoy).Should().ContainSingle(
            "control positivo: una carga de página con «Solo activas» ya no traería la fila editada");
    }

    /// <summary>
    /// Si la relectura de la fila falla, el guardado ya es firme: no se enseña error y se hace lo
    /// que se hacía antes, recargar la lista.
    /// </summary>
    [Fact]
    public async Task Si_la_relectura_de_la_fila_falla_se_recarga_la_lista_sin_ensenar_error()
    {
        var norte = Visita("Centro Norte");
        var mediator = MediadorQueEdita(UnaSemanaMas, norte, Visita("Planta Zaragoza"));
        mediator.FallarConsultaPorId = true;
        var cut = Renderizar(mediator);
        cut.WaitForAssertion(() => CentrosDeLasFilas(cut).Should().HaveCount(2));
        await AtajoAsync(cut, "j");
        var paginaAntes = ConsultasDePagina(mediator);

        await EditarLaFilaEnfocadaYGuardarAsync(cut);

        mediator.ConsultasDeLista.Should().Contain(c => c.VisitaId == norte.Id, "control positivo: la relectura se intentó");
        cut.WaitForAssertion(() => BotonVistaRapida(cut, "Centro Norte").TextContent.Trim().Should().Be(TextoFechasEsperado(UnaSemanaMas(norte)),
            "la recarga de página trae el dato guardado"));
        ConsultasDePagina(mediator).Should().Be(paginaAntes + 1, "una recarga de página, como antes de este cambio");
        cut.Markup.Should().NotContain("Fallo simulado");
        Services.GetRequiredService<ToastService>().Mensajes.Should().NotContain(m => m.Tono == TonoToast.Error);
    }
}
