using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La rejilla de Incidencias conserva la selección y la fila enfocada cuando QuickGrid repite por su cuenta una petición
/// idéntica con las mismas filas (PR8), pero esa guarda no puede tragarse una lista nueva: con otro filtro, otro orden, otro
/// tamaño de página u otras filas se empieza de cero. Cada caso parte de una fila enfocada y marcada (atajos j y x).
/// </summary>
public partial class IncidenciasGen2Tests
{
    private async Task<IRenderedComponent<CaeManager.Web.Features.Incidencias.Pages.Incidencias>> RenderizarConUnaFilaMarcadaAsync(MediadorControlado mediador)
    {
        var cut = Renderizar(mediador);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Centro Alfa"));

        var atajos = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("x"));

        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Contain("1 seleccionado", "punto de partida: la fila está marcada");
        cut.FindAll("tr.fila-enfocada").Should().ContainSingle("punto de partida: la fila está enfocada");
        return cut;
    }

    private static void DebeEmpezarDeCero(IRenderedComponent<CaeManager.Web.Features.Incidencias.Pages.Incidencias> cut, string motivo)
    {
        cut.WaitForAssertion(() =>
        {
            cut.FindAll(".barra-acciones-lote-cantidad").Should().BeEmpty($"{motivo}: la selección no sobrevive a una lista nueva");
            cut.FindAll("tr.fila-enfocada").Should().BeEmpty($"{motivo}: el foco tampoco");
        });
    }

    [Fact]
    public async Task Cambiar_el_filtro_de_estado_con_las_mismas_filas_limpia_la_seleccion_y_el_foco()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")] };
        var cut = await RenderizarConUnaFilaMarcadaAsync(mediador);

        await cut.BotonDeFranja("Sin resolver").ClickAsync(new());

        cut.WaitForAssertion(() => mediador.Consultas.Last().Resuelta.Should().BeFalse());
        DebeEmpezarDeCero(cut, "otro filtro, aunque devuelva las mismas filas");
    }

    [Fact]
    public async Task Cambiar_el_orden_con_las_mismas_filas_limpia_la_seleccion_y_el_foco()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")] };
        var cut = await RenderizarConUnaFilaMarcadaAsync(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.FindAll("th .col-title").Single(t => t.TextContent.Trim() == "Fecha").ClickAsync(new());

        cut.WaitForAssertion(() => ConsultasDeLista(mediador).Should().BeGreaterThan(consultasAntes, "ordenar pide la lista otra vez"));
        mediador.Consultas.Last().OrdenarPor.Should().NotBeNullOrEmpty("la consulta nueva lleva el orden pedido");
        DebeEmpezarDeCero(cut, "otro orden, aunque devuelva las mismas filas");
    }

    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_con_las_mismas_filas_limpia_la_seleccion_y_el_foco()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")] };
        var cut = await RenderizarConUnaFilaMarcadaAsync(mediador);

        await cut.Find(".pie-lista-opciones select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        cut.WaitForAssertion(() => mediador.Consultas.Last().TamanoPagina.Should().Be(50));
        DebeEmpezarDeCero(cut, "otro tamaño de página, aunque devuelva las mismas filas");
    }

    [Fact]
    public async Task Una_recarga_con_los_mismos_ids_en_otro_orden_limpia_la_seleccion_y_el_foco()
    {
        var mediador = new MediadorControlado { Filas = [Fila(IdAlfa, "Centro Alfa"), Fila(IdBeta, "Centro Beta")] };
        var cut = await RenderizarConUnaFilaMarcadaAsync(mediador);

        // Misma clave de consulta (página, tamaño, búsqueda, estado y orden), mismas filas pero en otro orden: es otra lista.
        // «Marcar resuelta» recarga por el camino normal de la pantalla (RecargarAsync), sin cambiar nada de la clave.
        mediador.Filas = [Fila(IdBeta, "Centro Beta"), Fila(IdAlfa, "Centro Alfa")];
        await cut.FindAll("table .menu-acciones-disparador")[1].ClickAsync(new());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Marcar resuelta").ClickAsync(new());

        DebeEmpezarDeCero(cut, "otras filas con la misma clave de consulta");
    }
}
