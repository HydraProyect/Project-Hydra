using Bunit;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Patrón de listados sin menú «⋯» en Proyectos (adopción E, 2026-10-09): lápiz en la cabecera
/// del panel, tecla «e» y recuento de técnicos con su ventana de contexto. El clic en la fila y
/// el icono 360 están junto a los tests del panel, en el fichero principal.
/// </summary>
public partial class ProyectosGen2Tests
{
    private static readonly Guid TrabajadorCarlaId = Guid.Parse("55555555-5555-5555-5555-555555555551");
    private static readonly Guid TrabajadorPaulaId = Guid.Parse("55555555-5555-5555-5555-555555555552");

    private static readonly ProyectoListaDto ProyectoConTecnicos = ProyectoAbierto with
    {
        TecnicosActivos =
        [
            new TecnicoActivoListaDto(TrabajadorCarlaId, "Carla Molina Ríos"),
            new TecnicoActivoListaDto(TrabajadorPaulaId, "Paula Campos Lara")
        ]
    };

    private static string PestanaActiva(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll("aside.panel-proyecto button[role=tab]").Single(b => b.GetAttribute("aria-selected") == "true").TextContent.Trim();

    private static bool EnEdicion(IRenderedComponent<Proyectos> cut) =>
        cut.FindComponents<CaeManager.Web.Components.DesignSystem.CampoTexto>().Any(c => c.Instance.Etiqueta == "Nombre");

    // ------------------------------------------------------------------ lápiz de la cabecera

    [Fact]
    public async Task El_lapiz_de_la_cabecera_edita_desde_cualquier_pestana_y_lleva_a_Informacion()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        await cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Técnicos").ClickAsync(new MouseEventArgs());
        PestanaActiva(cut).Should().Be("Técnicos", "el test necesita partir de otra pestaña");

        await Lapiz(cut).ClickAsync(new MouseEventArgs());

        PestanaActiva(cut).Should().Be("Información", "el formulario vive en esa pestaña");
        EnEdicion(cut).Should().BeTrue();
        cut.FindAll("aside.panel-proyecto .cabecera-panel-proyecto button[aria-label='Editar la información del proyecto']")
            .Should().BeEmpty("en edición el lápiz no se repite: guardar y cancelar están en el formulario");
    }

    [Fact]
    public async Task Consulta_no_ve_el_lapiz_y_sigue_viendo_el_icono_360_del_panel()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);

        cut.FindAll("aside.panel-proyecto .cabecera-panel-proyecto a.boton-360-pagina").Should().ContainSingle("la cabecera está pintada");
        cut.FindAll("aside.panel-proyecto .cabecera-panel-proyecto button[aria-label='Editar la información del proyecto']").Should().BeEmpty();
    }

    // ------------------------------------------------------------------ tecla «e»

    [Fact]
    public async Task La_tecla_e_abre_en_edicion_el_proyecto_de_la_fila_enfocada()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto2.Nombre], "el test necesita el foco en la segunda fila");

        await Atajo(cut, "e");

        cut.WaitForAssertion(() => PanelAbiertoEn(cut, ProyectoAbierto2).Should().BeTrue());
        ValorDelCampo(cut, "Nombre").Should().Be(ProyectoAbierto2.Nombre, "«e» deja el panel en edición");
    }

    [Fact]
    public async Task La_tecla_e_sin_fila_enfocada_edita_el_proyecto_del_panel_abierto_y_sin_panel_no_hace_nada()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();

        await Atajo(cut, "e");
        PanelDeDetalleAbierto(cut).Should().BeFalse("sin fila enfocada ni panel no hay nada que editar");

        await AbrirDetalle(cut, ProyectoAbierto);
        FilasEnfocadas(cut).Should().BeEmpty();
        await Atajo(cut, "e");

        ValorDelCampo(cut, "Nombre").Should().Be(ProyectoAbierto.Nombre);
    }

    [Fact]
    public async Task La_tecla_e_a_quien_solo_consulta_le_abre_el_panel_en_lectura()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();
        await Atajo(cut, "j");

        await Atajo(cut, "e");

        cut.WaitForAssertion(() => PanelAbiertoEn(cut, ProyectoAbierto).Should().BeTrue("el panel sí se abre"));
        cut.Find("aside.panel-proyecto .rejilla-info-proyecto").Should().NotBeNull();
        EnEdicion(cut).Should().BeFalse("el rol no puede escribir: «e» no abre un formulario que el lápiz no le ofrece");
    }

    [Fact]
    public async Task La_tecla_e_sobre_el_proyecto_que_ya_se_edita_no_pisa_lo_escrito()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto.Nombre], "el foco está en la fila del proyecto que se edita");

        await Atajo(cut, "e");

        PreguntaAbierta(cut).Should().BeFalse("no se abandona nada");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre");

        // Y sigue contando como cambio sin guardar: si «e» reabriera la edición, lo escrito pasaría
        // a ser el punto de partida y cerrar el panel lo tiraría sin preguntar.
        var cierre = cut.Find("aside.panel-proyecto button.cerrar-panel-proyecto").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("lo escrito sigue siendo un cambio sin guardar"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await cierre.WaitAsync(Paciencia);
    }

    [Fact]
    public async Task La_tecla_e_con_un_alta_de_tecnico_a_medias_en_ese_proyecto_pregunta_antes_de_tirarla()
    {
        var cut = await AbrirElAltaDeTecnicoAsync();
        await CambiarLaFechaDeAltaAsync(cut);

        // Sin fila enfocada, «e» edita el proyecto del panel. Sin await: queda pendiente del aviso.
        var tecla = Atajo(cut, "e");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("pasar a editar la información cierra el alta de técnico"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await tecla.WaitAsync(Paciencia);

        ValorDelCampo(cut, "Fecha de alta").Should().Be("2020-01-01", "«Seguir editando» conserva el alta a medias");
        EnEdicion(cut).Should().BeFalse();
    }

    [Fact]
    public async Task Un_clic_en_la_fila_del_proyecto_ya_abierto_no_devuelve_el_panel_a_Informacion()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoAbierto2];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoAbierto);
        await cut.FindAll("button[role=tab]").Single(b => b.TextContent.Trim() == "Técnicos").ClickAsync(new MouseEventArgs());

        await FilaDe(cut, ProyectoAbierto).ClickAsync(new MouseEventArgs());

        PestanaActiva(cut).Should().Be("Técnicos");
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().ContainSingle("el detalle ya estaba cargado");

        await FilaDe(cut, ProyectoAbierto2).ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => PanelAbiertoEn(cut, ProyectoAbierto2).Should().BeTrue("la fila de otro proyecto sí cambia el panel"));
    }

    [Fact]
    public async Task La_tecla_e_sobre_otra_fila_con_la_edicion_a_medias_pregunta_antes_de_cambiar_de_proyecto()
    {
        var cut = await AbrirLaEdicionDelDetalleAsync();
        await EscribirEnElPanelAsync(cut, "Otro nombre");
        await Atajo(cut, "j");
        await Atajo(cut, "j");
        FilasEnfocadas(cut).Should().Equal([ProyectoAbierto2.Nombre]);

        // Sin await: la tecla queda pendiente de la respuesta del aviso; se afirma antes de esperarla.
        var tecla = Atajo(cut, "e");
        cut.WaitForAssertion(() => PreguntaAbierta(cut).Should().BeTrue("editar otro proyecto tira lo escrito en este"));
        await PulsarEnLaPreguntaAsync(cut, "Seguir editando");
        await tecla.WaitAsync(Paciencia);

        PanelAbiertoEn(cut, ProyectoAbierto).Should().BeTrue("«Seguir editando» no cambia de proyecto");
        ValorDelCampo(cut, "Nombre").Should().Be("Otro nombre");
        _mediator.Enviados.OfType<ActualizarProyectoCommand>().Should().BeEmpty();
    }

    // ------------------------------------------------------------------ recuento de técnicos

    [Fact]
    public async Task El_recuento_de_tecnicos_nombra_a_los_activos_y_un_proyecto_sin_tecnicos_muestra_cero_sin_ventana()
    {
        _mediator.Proyectos = [ProyectoConTecnicos, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        var celda = FilaDe(cut, ProyectoConTecnicos).QuerySelector(".celda-tecnicos-proyecto")!;
        var disparador = celda.QuerySelector(".ventana-contexto-disparador")!;
        disparador.TextContent.Trim().Should().Be("2");
        disparador.GetAttribute("aria-label").Should().Be("2 técnicos activos", "un número a secas no dice qué cuenta");
        celda.QuerySelectorAll(".ventana-contexto-elemento-texto").Select(e => e.TextContent.Trim())
            .Should().Equal(["Carla Molina Ríos", "Paula Campos Lara"], "las personas se escriben «Nombre Apellidos»");

        var celdaVacia = FilaDe(cut, ProyectoCerrado).QuerySelector(".celda-tecnicos-proyecto")!;
        celdaVacia.TextContent.Trim().Should().Be("0");
        celdaVacia.QuerySelectorAll(".ventana-contexto").Should().BeEmpty("sin técnicos no hay lista que enseñar");
    }

    [Fact]
    public async Task Un_tecnico_de_la_ventana_abre_el_panel_del_proyecto_en_la_pestana_Tecnicos()
    {
        _mediator.Proyectos = [ProyectoConTecnicos, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        await FilaDe(cut, ProyectoConTecnicos).QuerySelectorAll(".ventana-contexto-elemento")
            .Single(e => e.TextContent.Contains("Paula Campos Lara")).ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => PanelAbiertoEn(cut, ProyectoConTecnicos).Should().BeTrue());
        PestanaActiva(cut).Should().Be("Técnicos");
        _mediator.Enviados.OfType<ObtenerTecnicosProyectoQuery>().Should().ContainSingle("la pestaña carga su lista")
            .Which.ProyectoId.Should().Be(ProyectoConTecnicos.Id);
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().ContainSingle("el clic del técnico no sube a la fila y no pide el detalle dos veces");
    }

    [Fact]
    public async Task Un_tecnico_de_la_ventana_con_el_panel_de_ese_proyecto_ya_abierto_solo_cambia_de_pestana()
    {
        _mediator.Proyectos = [ProyectoConTecnicos];
        var cut = await RenderizarConClienteAsync();
        await AbrirDetalle(cut, ProyectoConTecnicos);
        PestanaActiva(cut).Should().Be("Información");

        await FilaDe(cut, ProyectoConTecnicos).QuerySelectorAll(".ventana-contexto-elemento")
            .Single(e => e.TextContent.Contains("Carla Molina Ríos")).ClickAsync(new MouseEventArgs());

        PestanaActiva(cut).Should().Be("Técnicos");
        _mediator.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().ContainSingle("el detalle ya estaba cargado");
    }
}
