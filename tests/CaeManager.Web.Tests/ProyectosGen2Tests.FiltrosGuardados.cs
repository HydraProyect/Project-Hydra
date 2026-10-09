using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Aplicar un filtro guardado en Proyectos con el panel de detalle abierto y algo a medias en él. Lo demás de
/// los filtros guardados de esta página (qué guarda, qué quita, cuándo carga) está en
/// <see cref="FiltrosGuardadosEnListadosTests"/>; aquí hace falta el panel editado, que es lo que monta esta clase.
///
/// <para>
/// Aplicar un filtro guardado pregunta UNA vez y antes de tocar nada, cambie o no de Cliente empresarial. El
/// gesto manual equivalente también pregunta: cambiar de Cliente empresarial cierra el panel
/// (<c>Aviso_cambiar_de_Cliente_empresarial_…</c>), y cambiar la búsqueda o el estado navega y el aviso de la
/// página detiene esa navegación (<see cref="Escribir_en_el_buscador_con_la_edicion_a_medias_pregunta_al_navegar"/>).
/// La diferencia es quién repite la navegación al descartar: si la detuviera el aviso, la repetiría él y sin
/// reemplazo; preguntando antes, la navegación del filtro sale una vez y con reemplazo.
/// </para>
/// </summary>
public partial class ProyectosGen2Tests
{
    private void ConFiltroGuardado(string nombre, string valoresJson) =>
        _mediator.FiltrosGuardados.Add(new FiltroGuardadoDto(Guid.NewGuid(), nombre, valoresJson, DateTime.UtcNow));

    /// <summary>Abre «Más filtros» y pulsa el filtro guardado con ese nombre. La tarea puede quedar pendiente de una pregunta.</summary>
    private static async Task AplicarFiltroGuardadoAsync(IRenderedComponent<Proyectos> cut, string nombre)
    {
        var disparador = DisparadorPastilla(cut, "Más filtros");
        if (disparador.GetAttribute("aria-expanded") != "true")
            await disparador.ClickAsync(new MouseEventArgs());

        await cut.FindAll(".barra-filtros-pastillas [role=menuitem]")
            .Single(i => i.TextContent.Trim() == nombre).ClickAsync(new MouseEventArgs());
    }

    private static string ValorDelBuscador(IRenderedComponent<Proyectos> cut) =>
        cut.FindComponents<BarraFiltros>().Single().Instance.Busqueda;

    private int CargasDeProyectosDe(Guid clienteId) =>
        _mediator.Enviados.OfType<ObtenerProyectosQuery>().Count(q => q.ClienteId == clienteId);

    /// <summary>
    /// La referencia: el cambio MANUAL de la búsqueda con la edición del panel a medias pregunta «¿Salir sin
    /// guardar?», porque navega y el aviso de la página detiene la navegación. Por eso aplicar un filtro
    /// guardado del mismo Cliente empresarial no puede dejar de preguntar: solo elige preguntar antes de navegar.
    /// </summary>
    [Fact]
    public async Task Escribir_en_el_buscador_con_la_edicion_a_medias_pregunta_al_navegar()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var uriAntes = Uri;

        await cut.Find("input[data-filtro-pantalla]").InputAsync(new ChangeEventArgs { Value = "nave" });

        PreguntaAbierta(cut).Should().BeTrue("la búsqueda viaja en la URL y el aviso detiene la navegación");
        Uri.Should().Be(uriAntes, "detenida, la URL no cambia");
    }

    [Fact]
    public async Task Filtro_guardado_del_mismo_Cliente_empresarial_con_la_edicion_sin_tocar_no_pregunta_y_el_panel_sigue_abierto()
    {
        ConFiltroGuardado("Naves de A", $"{{\"cliente\":\"{ClienteId}\",\"q\":\"nave\"}}");
        var cut = await AbrirLaEdicionDelDetalleAsync();

        await ComprobarQueTerminaSinPreguntarAsync(cut, AplicarFiltroGuardadoAsync(cut, "Naves de A"), "no cambia de Cliente empresarial ni hay nada escrito");

        Uri.Should().Contain($"cliente={ClienteId}").And.Contain("q=nave");
        ValorDelBuscador(cut).Should().Be("nave");
        PanelDeDetalleAbierto(cut).Should().BeTrue("la búsqueda y el estado no cierran el panel");
        ValorDelCampo(cut, "Nombre").Should().Be(ProyectoAbierto.Nombre, "y la edición sigue abierta");
        CargasDeProyectosDe(ClienteId).Should().Be(1, "el mismo Cliente empresarial no se vuelve a cargar");
    }

    [Fact]
    public async Task Filtro_guardado_del_mismo_Cliente_empresarial_con_la_edicion_a_medias_pregunta_seguir_no_cambia_nada_y_descartar_lo_aplica()
    {
        ConFiltroGuardado("Naves de A", $"{{\"cliente\":\"{ClienteId}\",\"q\":\"nave\"}}");
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var uriAntes = Uri;

        var gesto = AplicarFiltroGuardadoAsync(cut, "Naves de A");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("aplicar la vista reescribe la URL: se pregunta antes"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await gesto.WaitAsync(Paciencia);

        PreguntaAbierta(cut).Should().BeFalse();
        Uri.Should().Be(uriAntes, "«Seguir editando» no navega");
        ValorDelBuscador(cut).Should().BeEmpty("el campo sigue diciendo lo que dice la URL");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");

        var otraVez = AplicarFiltroGuardadoAsync(cut, "Naves de A");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await otraVez.WaitAsync(Paciencia);

        cut.WaitForAssertion(() => Uri.Should().Contain("q=nave", "descartado lo escrito, el filtro se aplica"));
        ValorDelBuscador(cut).Should().Be("nave");
        PreguntaAbierta(cut).Should().BeFalse("una sola pregunta");
        PanelDeDetalleAbierto(cut).Should().BeTrue("el mismo Cliente empresarial no cierra el panel");
    }

    /// <summary>
    /// Con el mismo Cliente empresarial y el panel a medias, la pregunta la hace la página ANTES de navegar. Si la
    /// hiciera el aviso al detener la navegación, «Salir y descartar» la repetiría él, sin reemplazo: una entrada
    /// de más en el historial, y «atrás» volvería a la misma lista con otros filtros en vez de salir de ella.
    /// </summary>
    [Fact]
    public async Task Filtro_guardado_del_mismo_Cliente_empresarial_con_la_edicion_a_medias_al_descartar_navega_una_vez_y_con_reemplazo()
    {
        ConFiltroGuardado("Naves de A", $"{{\"cliente\":\"{ClienteId}\",\"q\":\"nave\"}}");
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var historial = ((BunitNavigationManager)Services.GetRequiredService<NavigationManager>()).History;

        var gesto = AplicarFiltroGuardadoAsync(cut, "Naves de A");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await gesto.WaitAsync(Paciencia);
        cut.WaitForAssertion(() => Uri.Should().Contain("q=nave", "control positivo: el filtro se aplicó"));

        var delFiltro = historial.Where(h => h.Uri.Contains("q=nave")).ToList();
        delFiltro.Should().NotBeEmpty("control positivo: el historial de bUnit registra la navegación del filtro")
            .And.OnlyContain(h => h.Options.ReplaceHistoryEntry, "un filtro sustituye la entrada del historial, no añade otra");
        delFiltro.Should().ContainSingle("la navegación del filtro sale una vez: nadie la detiene y la repite");
    }

    [Fact]
    public async Task Filtro_guardado_de_otro_Cliente_empresarial_con_la_edicion_a_medias_pregunta_seguir_deja_campos_url_y_datos_y_descartar_cambia()
    {
        ConFiltroGuardado("Cámaras de B", $"{{\"cliente\":\"{ClienteBId}\",\"q\":\"cámaras\"}}");
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        var uriAntes = Uri;

        var gesto = AplicarFiltroGuardadoAsync(cut, "Cámaras de B");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("cambiar de Cliente empresarial cierra el panel"));
        PanelDeDetalleAbierto(cut).Should().BeTrue("mientras pregunta, el panel sigue ahí");
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await gesto.WaitAsync(Paciencia);

        Uri.Should().Be(uriAntes, "«Seguir editando» no navega");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Refrielectric S.L.");
        ValorDelBuscador(cut).Should().BeEmpty();
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre", "y conserva lo escrito");
        NombresEnLaTabla(cut).Should().Contain(ProyectoAbierto.Nombre, "la lista sigue siendo la del Cliente empresarial que había");
        CargasDeProyectosDe(ClienteBId).Should().Be(0, "no se ha pedido nada del otro Cliente empresarial");

        var otraVez = AplicarFiltroGuardadoAsync(cut, "Cámaras de B");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue());
        await PulsarEnLaPreguntaAsync(cut, "Salir y descartar");
        await otraVez.WaitAsync(Paciencia);

        cut.WaitForAssertion(() => Uri.Should().Contain($"cliente={ClienteBId}"));
        PreguntaAbierta(cut).Should().BeFalse("una sola pregunta: la navegación sale ya sin nada pendiente");
        PanelDeDetalleAbierto(cut).Should().BeFalse("el panel era de un proyecto del Cliente empresarial anterior");
        SelectorDeCliente(cut).GetAttribute("aria-label").Should().Be("Cliente: Frigoríficos Arcos S.A.");
        ValorDelBuscador(cut).Should().Be("cámaras");
        NombresEnLaTabla(cut).Should().Equal([ProyectoDeB.Nombre]);
        CargasDeProyectosDe(ClienteBId).Should().Be(1, "una carga, no dos");
    }
}
