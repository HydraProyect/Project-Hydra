using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class ProyectosGen2Tests
{
    /// <summary>
    /// Resaltado de la búsqueda en /proyectos. La pantalla filtra en memoria
    /// (<c>FiltroProyectos.CumpleBusqueda</c>) por nombre del Proyecto y nombre del Centro, con el
    /// término recortado y sin distinguir acentos: se marcan los dos con ese mismo término. Aquí
    /// el filtro es el de verdad, no un doble.
    /// </summary>
    [Fact]
    public async Task Con_busqueda_la_fila_marca_el_nombre_y_el_Centro_sin_distinguir_acentos()
    {
        // «Ampliación línea de frío — nave 3», en «Centro Logístico Norte».
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        cut.WaitForAssertion(() => FilaDe(cut, ProyectoAbierto));
        cut.FindAll("mark").Should().BeEmpty("sin búsqueda no hay marcas");
        var antes = FilaDe(cut, ProyectoAbierto).Foto();
        antes.Atributos.Should().Contain(ProyectoAbierto.Nombre, "control: la fila tiene nombres accesibles que comparar");

        await cut.BuscarEnLaBarraAsync("ampliacion");
        cut.WaitForAssertion(() => FilaDe(cut, ProyectoAbierto).DebeMarcarSolo(antes, "Ampliación"));

        await cut.BuscarEnLaBarraAsync("LOGISTICO");
        cut.WaitForAssertion(() => FilaDe(cut, ProyectoAbierto).DebeMarcarSolo(antes, "Logístico"));

        // El filtro recorta el término; la marca usa el mismo término recortado.
        await cut.BuscarEnLaBarraAsync("  frio  ");
        cut.WaitForAssertion(() => FilaDe(cut, ProyectoAbierto).DebeMarcarSolo(antes, "frío"));
    }

    /// <summary>
    /// Medio emoji pegado en el buscador es un sustituto suelto: no es Unicode válido y
    /// <c>string.Normalize</c> lo rechaza. Esta pantalla filtra en memoria dentro del render, así
    /// que una excepción ahí tiraría la página entera. El término no casa con ningún Proyecto y la
    /// pantalla sigue pintada y respondiendo.
    /// </summary>
    [Fact]
    public async Task Un_sustituto_suelto_en_el_buscador_no_casa_con_nada_y_la_pagina_sigue_pintada()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();
        cut.WaitForAssertion(() => FilaDe(cut, ProyectoAbierto));
        var suelto = new string((char)0xD83C, 1);

        await cut.BuscarEnLaBarraAsync(suelto);

        cut.WaitForAssertion(() => cut.FindAll("tbody .nombre-proyecto").Should().BeEmpty(
            "un término que no es Unicode válido no casa con ningún Proyecto"));
        cut.FindComponents<BarraFiltros>().Should().ContainSingle("la página sigue pintada, con su barra de filtros");

        // Control: la pantalla sigue viva y vuelve a filtrar y a marcar con un término válido.
        await cut.BuscarEnLaBarraAsync("ampliacion");
        cut.WaitForAssertion(() => FilaDe(cut, ProyectoAbierto).Marcas().Should().Equal("Ampliación"));
    }
}
