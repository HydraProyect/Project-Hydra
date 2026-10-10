using Bunit;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Proyectos;
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
    /// que una excepción ahí tiraría la página entera. El filtro que comparten la página y su
    /// exportación no lanza y no deja pasar ningún Proyecto.
    ///
    /// Es la prueba sensible de la guarda de <c>TextoDeBusqueda.Contiene</c> en esta pantalla: sin
    /// ella, este test falla con <c>ArgumentException</c> (medido por mutación). El test de página
    /// de abajo no la ejercita.
    /// </summary>
    [Fact]
    public void El_filtro_de_Proyectos_no_lanza_con_un_sustituto_suelto_y_no_deja_pasar_nada()
    {
        var suelto = new string((char)0xD83C, 1);

        FiltroProyectos.CumpleBusqueda(ProyectoAbierto, suelto).Should().BeFalse();
        FiltroProyectos.Cumple(ProyectoAbierto, estados: null, "ampliacion " + suelto).Should().BeFalse();
        FiltroProyectos.CumpleBusqueda(ProyectoAbierto, "ampliacion").Should().BeTrue("control: un término válido sí casa");
    }

    /// <summary>
    /// El mismo sustituto suelto, escrito en el buscador de la página: la lista queda sin filas, la
    /// pantalla sigue pintada y vuelve a filtrar con un término válido.
    ///
    /// <b>Qué NO mide.</b> Aquí el sustituto no llega al filtro: la búsqueda viaja a la URL
    /// (<c>?q=</c>) y vuelve de ella, y en ese viaje .NET lo cambia por U+FFFD, que sí es Unicode
    /// válido (medido: a la barra le llega «FFFD»). Por eso este test sigue en verde aunque se quite
    /// la guarda de <c>TextoDeBusqueda.Contiene</c>; la guarda la fija el test de arriba. Tampoco
    /// dice qué recibe el servidor de un navegador real.
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
