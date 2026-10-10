using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectos;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Proyectos;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Listado de Proyectos, cierre de listados J (2026-10-10): columnas, motivo bajo el estado, «Reabrir» como
/// acción rápida de la fila, aviso de fin previsto superado, paginación en servidor y el Cliente empresarial
/// del alta, que va en el formulario porque la lista no obliga a elegir uno.
///
/// <para>
/// El mediador aplica en memoria el contrato de <c>ObtenerProyectosQuery</c>; lo que se afirma aquí es qué
/// pide la pantalla y qué pinta con lo que recibe, no el SQL.
/// </para>
/// </summary>
public partial class ProyectosGen2Tests
{
    private static List<ProyectoListaDto> Obras(int cuantas) =>
        Enumerable.Range(1, cuantas)
            .Select(i => ProyectoAbierto with { Id = Guid.NewGuid(), Nombre = $"Obra {i:00}" })
            .ToList();

    private static Task PulsarSiguienteAsync(IRenderedComponent<Proyectos> cut) =>
        cut.FindAll(".paginador button").Single(b => b.TextContent.Trim() == "Siguiente").ClickAsync(new MouseEventArgs());

    private static string MotivoDe(IRenderedComponent<Proyectos> cut, ProyectoListaDto proyecto) =>
        FilaDe(cut, proyecto).QuerySelector("td.col-estado .estado-fila-motivo")!.TextContent.Trim();

    // ------------------------------------------------------------------ columnas

    [Fact]
    public async Task Las_columnas_son_Proyecto_Fechas_Tecnicos_Plazo_transcurrido_y_Estado_en_ese_orden()
    {
        _mediator.Proyectos = [ProyectoAbierto];
        var cut = await RenderizarConClienteAsync();

        cut.FindAll("table.tabla-proyectos thead th").Select(th => th.TextContent.Trim())
            .Should().Equal("Proyecto", "Fechas", "Técnicos", "Plazo transcurrido", "Estado", "Acciones");

        var celdas = FilaDe(cut, ProyectoAbierto).QuerySelectorAll("td");
        celdas[0].QuerySelector(".nombre-proyecto").Should().NotBeNull();
        celdas[1].QuerySelector(".proyecto-plazo").Should().NotBeNull("la segunda columna son las fechas");
        celdas[2].ClassList.Should().Contain("celda-tecnicos-proyecto");
        celdas[3].ClassList.Should().Contain("col-plazo-transcurrido");
        celdas[4].ClassList.Should().Contain("col-estado", "el estado cae en la columna de ancho fijo común a los listados");
        celdas[4].QuerySelector(".estado-fila").Should().NotBeNull();
        cut.Find("thead th.col-estado").TextContent.Trim().Should().Be("Estado");
    }

    // ------------------------------------------------------------------ motivo bajo el estado

    /// <summary>
    /// El motivo dice lo que la pastilla no dice: cuánto lleva abierto o cuánto duró, con la cuenta inclusiva
    /// de la ficha 360 (día de inicio y día de cierre cuentan). «Hoy» es el día de negocio.
    /// </summary>
    [Fact]
    public async Task El_motivo_bajo_el_estado_dice_los_dias_abierto_o_lo_que_duro_en_singular_y_en_plural()
    {
        var hoy = DiaDeNegocio.Hoy();
        var deHoy = ProyectoAbierto2 with { FechaInicio = hoy };
        var deHaceDiez = ProyectoAbierto with { FechaInicio = hoy.AddDays(-9), FechaFinPrevista = null };
        var sinEmpezar = ProyectoDeB with { FechaInicio = hoy.AddDays(5) };
        var deUnDia = ProyectoCerrado with
        {
            Id = Guid.NewGuid(),
            Nombre = "Reparación urgente de cuadro",
            FechaInicio = new DateOnly(2026, 3, 4),
            FechaCierreReal = new DateOnly(2026, 3, 4),
        };
        _mediator.Proyectos = [sinEmpezar, deHoy, deHaceDiez, ProyectoCerrado, deUnDia];

        var cut = await RenderizarConClienteAsync();

        MotivoDe(cut, deHoy).Should().Be("1 día abierto");
        MotivoDe(cut, deHaceDiez).Should().Be("10 días abierto");
        MotivoDe(cut, sinEmpezar).Should().Be("Todavía no ha empezado");
        MotivoDe(cut, ProyectoCerrado).Should().Be("Duró 55 días", "del 9 de enero al 4 de marzo de 2026, contando los dos");
        MotivoDe(cut, deUnDia).Should().Be("Duró 1 día");
        FilaDe(cut, ProyectoCerrado).QuerySelector("td.col-estado")!.TextContent.Should().Contain("Cerrado");
        FilaDe(cut, deHoy).QuerySelector("td.col-estado [data-pieza=estado-correcto]").Should().NotBeNull(
            "abierto no pide acción: punto verde, sin pastilla de color");
    }

    // ------------------------------------------------------------------ fin previsto superado

    /// <summary>
    /// La barra de plazo se queda en el 100 % y no distingue «acaba hoy» de «lleva un mes de retraso»: la
    /// fila lo dice con el mismo texto que la ficha 360. Solo en Proyectos abiertos y pasado el día del fin.
    /// </summary>
    [Fact]
    public async Task Un_proyecto_abierto_pasado_de_su_fin_previsto_lo_dice_con_el_texto_de_la_ficha()
    {
        var hoy = DiaDeNegocio.Hoy();
        var retrasado = ProyectoAbierto with { FechaInicio = hoy.AddDays(-30), FechaFinPrevista = hoy.AddDays(-3) };
        var deAyer = ProyectoAbierto2 with { FechaInicio = hoy.AddDays(-30), FechaFinPrevista = hoy.AddDays(-1) };
        var acabaHoy = ProyectoDeB with { FechaInicio = hoy.AddDays(-30), FechaFinPrevista = hoy };
        var cerradoTarde = ProyectoCerrado with { FechaFinPrevista = new DateOnly(2026, 2, 1) };
        _mediator.Proyectos = [retrasado, deAyer, acabaHoy, cerradoTarde];

        var cut = await RenderizarConClienteAsync();

        FilaDe(cut, retrasado).QuerySelector(".proyecto-fin-superado")!.TextContent.Trim()
            .Should().Be("fin previsto superado hace 3 días");
        FilaDe(cut, deAyer).QuerySelector(".proyecto-fin-superado")!.TextContent.Trim()
            .Should().Be("fin previsto superado hace 1 día");
        FilaDe(cut, acabaHoy).QuerySelectorAll(".proyecto-fin-superado").Should().BeEmpty("el día del fin previsto todavía no es retraso");
        FilaDe(cut, cerradoTarde).QuerySelectorAll(".proyecto-fin-superado").Should().BeEmpty("cerrado, ya no hay nada que atender");
    }

    [Theory]
    [InlineData("2026-03-12", "2026-09-30", null, "2026-10-08", 8)]
    [InlineData("2026-03-12", "2026-10-07", null, "2026-10-08", 1)]
    [InlineData("2026-03-12", "2026-10-08", null, "2026-10-08", null)]         // el día del fin previsto no es retraso
    [InlineData("2026-03-12", "2026-11-30", null, "2026-10-08", null)]         // en plazo
    [InlineData("2026-03-12", null, null, "2026-10-08", null)]                 // sin fin previsto
    [InlineData("2026-01-09", "2026-02-01", "2026-03-04", "2026-10-08", null)] // cerrado, aunque cerrara tarde
    public void Dias_de_retraso(string inicio, string? fin, string? cierre, string hoy, int? esperado) =>
        PlazoProyecto.DiasDeRetraso(DateOnly.Parse(inicio), fin is null ? null : DateOnly.Parse(fin),
                cierre is null ? null : DateOnly.Parse(cierre), DateOnly.Parse(hoy))
            .Should().Be(esperado);

    // ------------------------------------------------------------------ «Reabrir» en la fila

    /// <summary>
    /// «Reabrir» es la acción rápida de la fila del Proyecto cerrado: el mismo comando, con la misma
    /// confirmación que el pie del panel, y sin abrir la vista rápida. «Eliminar» sigue solo en el pie.
    /// </summary>
    [Fact]
    public async Task Reabrir_de_la_fila_solo_esta_en_los_cerrados_pide_la_misma_confirmacion_y_no_abre_el_panel()
    {
        _mediator.Proyectos = [ProyectoAbierto, ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        FilaDe(cut, ProyectoAbierto).QuerySelectorAll(".proyectos-accion-rapida").Should().BeEmpty("un Proyecto abierto no se reabre");
        cut.FindAll("tbody td.col-acciones button").Select(b => b.TextContent.Trim())
            .Should().Equal(["Reabrir"], "la fila no lleva «Eliminar» ni «Cerrar proyecto»: esos viven en el pie del panel");
        var reabrir = FilaDe(cut, ProyectoCerrado).QuerySelector(".proyectos-accion-rapida")!;
        reabrir.GetAttribute("aria-label").Should().Be($"Reabrir el proyecto {ProyectoCerrado.Nombre}");

        await reabrir.ClickAsync(new MouseEventArgs());

        cut.FindAll("aside.panel-proyecto").Should().BeEmpty("la acción rápida no abre la vista rápida de la fila");
        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty("el primer clic solo pide confirmación");
        cut.Find("[role=dialog]").TextContent.Should()
            .Contain(ProyectoCerrado.Nombre)
            .And.Contain("(04/03/2026)", "la confirmación nombra la fecha de cierre que se pierde, como la del panel");

        await ConfirmarReapertura(cut);

        _mediator.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.Id.Should().Be(ProyectoCerrado.Id);
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => m.Tono == TonoToast.Exito)
            .Which.Mensaje.Should().StartWith("Proyecto reabierto");
    }

    [Fact]
    public async Task Consulta_no_ve_Reabrir_en_la_fila_de_un_proyecto_cerrado()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        _mediator.Proyectos = [ProyectoCerrado];
        var cut = await RenderizarConClienteAsync();

        FilaDe(cut, ProyectoCerrado).QuerySelector("td.col-acciones .boton-360").Should().NotBeNull(
            "la celda de acciones tiene que estar pintada para que la ausencia signifique algo");
        FilaDe(cut, ProyectoCerrado).QuerySelectorAll(".proyectos-accion-rapida").Should().BeEmpty();
    }

    // ------------------------------------------------------------------ paginación en servidor

    [Fact]
    public async Task La_lista_se_pide_por_paginas_y_el_contador_y_la_franja_dicen_el_total()
    {
        _mediator.Proyectos = Obras(45);
        var cut = await RenderizarConClienteAsync();

        UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(q => q.Pagina == 1 && q.TamanoPagina == 20);
        NombresEnLaTabla(cut).Should().HaveCount(20).And.StartWith("Obra 01");
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("45", "el contador es el total, no las filas de la página");
        cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(45);

        await PulsarSiguienteAsync(cut);

        UltimaConsultaDeProyectos.Pagina.Should().Be(2);
        NombresEnLaTabla(cut).Should().HaveCount(20).And.StartWith("Obra 21");

        await PulsarSiguienteAsync(cut);

        NombresEnLaTabla(cut).Should().Equal("Obra 41", "Obra 42", "Obra 43", "Obra 44", "Obra 45");
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task El_paginador_solo_aparece_cuando_la_lista_no_cabe_en_la_pagina_mas_pequena(int proyectos, bool conPaginador)
    {
        _mediator.Proyectos = Obras(proyectos);
        var cut = await RenderizarConClienteAsync();

        NombresEnLaTabla(cut).Should().HaveCount(20, "control positivo: la lista está pintada");
        cut.FindAll(".paginador").Any().Should().Be(conPaginador);
    }

    /// <summary>Un filtro cambia el conjunto: la página 2 del anterior no es la página 2 del nuevo.</summary>
    [Fact]
    public async Task Cambiar_el_estado_la_busqueda_o_el_Cliente_empresarial_vuelve_a_la_primera_pagina()
    {
        _mediator.Proyectos = Obras(45);
        _mediator.ProyectosClienteB = [ProyectoDeB];
        var cut = await RenderizarConClienteAsync();

        await PulsarSiguienteAsync(cut);
        UltimaConsultaDeProyectos.Pagina.Should().Be(2, "control positivo");
        await cut.BotonDeFranja("Abiertos").ClickAsync(new MouseEventArgs());
        UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(q => q.Pagina == 1 && q.SoloAbiertos == true);

        await PulsarSiguienteAsync(cut);
        UltimaConsultaDeProyectos.Pagina.Should().Be(2, "control positivo");
        await cut.Find("input[data-filtro-pantalla]").InputAsync(new ChangeEventArgs { Value = "Obra 4" });
        cut.WaitForAssertion(() => UltimaConsultaDeProyectos.Should()
            .Match<ObtenerProyectosQuery>(q => q.Pagina == 1 && q.Busqueda == "Obra 4"));
        cut.WaitForAssertion(() => NombresEnLaTabla(cut).Should().Equal("Obra 40", "Obra 41", "Obra 42", "Obra 43", "Obra 44", "Obra 45"));

        await cut.Find("input[data-filtro-pantalla]").InputAsync(new ChangeEventArgs { Value = "" });
        cut.WaitForAssertion(() => NombresEnLaTabla(cut).Should().HaveCount(20));
        await PulsarSiguienteAsync(cut);
        UltimaConsultaDeProyectos.Pagina.Should().Be(2, "control positivo");
        await ElegirCliente(cut, Guid.Empty);
        UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(q => q.Pagina == 1 && q.ClienteId == null);
    }

    /// <summary>
    /// La última fila de la última página desaparece (aquí, eliminada desde el pie del panel): la página
    /// pedida vuelve vacía con un total que no lo es. La lista retrocede a la última que tiene filas en
    /// vez de pintar «Sin proyectos» sobre un Cliente empresarial que tiene veinte.
    /// </summary>
    [Fact]
    public async Task Si_la_pagina_pedida_se_queda_sin_filas_la_lista_retrocede_a_la_ultima_que_las_tiene()
    {
        _mediator.Proyectos = Obras(21);
        var ultima = _mediator.Proyectos[^1];
        var cut = await RenderizarConClienteAsync();
        await PulsarSiguienteAsync(cut);
        NombresEnLaTabla(cut).Should().Equal(["Obra 21"], "control positivo: la segunda página tiene una sola fila");

        await AbrirDetalle(cut, ultima);
        await BotonConTexto(cut, ".pie-panel-proyecto button", "Eliminar").ClickAsync(new MouseEventArgs());
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => NombresEnLaTabla(cut).Should().HaveCount(20).And.StartWith("Obra 01"));
        cut.Markup.Should().NotContain("Sin proyectos");
        UltimaConsultaDeProyectos.Pagina.Should().Be(1);
        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("20");
    }

    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_vuelve_a_pedir_la_primera_con_ese_tamano()
    {
        _mediator.Proyectos = Obras(45);
        var cut = await RenderizarConClienteAsync();
        await PulsarSiguienteAsync(cut);

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        UltimaConsultaDeProyectos.Should().Match<ObtenerProyectosQuery>(q => q.Pagina == 1 && q.TamanoPagina == 50);
        NombresEnLaTabla(cut).Should().HaveCount(45);
        cut.FindAll(".paginador-tamano-select").Should().ContainSingle(
            "con todo en una página el paginador se queda: sin él no habría cómo volver a bajar el tamaño");
    }

    // ------------------------------------------------------------------ estado de la franja → consulta

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("abiertos", true)]
    [InlineData("cerrados", false)]
    [InlineData("abiertos,cerrados", null)]
    [InlineData("cerrados,abiertos", null)]
    [InlineData("archivados", null)]
    public void La_seleccion_de_estados_se_traduce_al_filtro_de_la_consulta(string? seleccion, bool? esperado) =>
        FiltroProyectos.SoloAbiertos(seleccion).Should().Be(esperado, "los dos marcados o ninguno enseñan todos");

    // ------------------------------------------------------------------ alta: el Cliente empresarial va en el formulario

    /// <summary>
    /// La lista no obliga a elegir Cliente empresarial, y <c>CrearProyectoCommand</c> lo exige con
    /// un Centro suyo: lo pide el formulario. Sin él no hay Centros que ofrecer ni se envía nada.
    /// </summary>
    [Fact]
    public async Task El_alta_sin_Cliente_empresarial_en_el_filtro_lo_pide_y_al_elegirlo_ofrece_sus_Centros()
    {
        _mediator.CentrosClienteA = [CentroDeA];
        _mediator.CentrosClienteB = [CentroDeB];
        var cut = Renderizar();
        await PulsarNuevoProyectoAsync(cut);

        OpcionesDelAlta(cut, "Cliente").Should().Equal("— Selecciona un Cliente —", "Refrielectric S.L.", "Frigoríficos Arcos S.A.");
        OpcionesDelAlta(cut, "Centro").Should().Equal(["Selecciona un centro…"], "sin Cliente empresarial no hay Centros que ofrecer");
        _mediator.Enviados.OfType<ObtenerCentrosParaSelectorQuery>().Should().BeEmpty();

        await EscribirNombreDelProyectoAsync(cut, "Montaje cámaras 2026");
        await BotonConTexto(cut, ".drawer-panel button", "Crear proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().BeEmpty();
        cut.Find(".drawer-panel [role=alert]").TextContent.Trim().Should().Be("Selecciona un Cliente.");

        await SelectDelAlta(cut, "Cliente").ChangeAsync(new ChangeEventArgs { Value = ClienteBId.ToString() });
        OpcionesDelAlta(cut, "Centro").Should().Equal("Selecciona un centro…", CentroDeB.Nombre);
        await SelectDelAlta(cut, "Centro").ChangeAsync(new ChangeEventArgs { Value = CentroDeB.Id.ToString() });
        await BotonConTexto(cut, ".drawer-panel button", "Crear proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().ContainSingle()
            .Which.Should().Match<CrearProyectoCommand>(c => c.ClienteId == ClienteBId && c.CentroId == CentroDeB.Id);
    }

    [Fact]
    public async Task El_alta_con_Cliente_empresarial_en_el_filtro_lo_trae_puesto_con_sus_Centros()
    {
        var cut = await AbrirNuevoProyectoAsync();

        SelectDelAlta(cut, "Cliente").GetAttribute("value").Should().Be(ClienteId.ToString());
        OpcionesDelAlta(cut, "Centro").Should().Equal("Selecciona un centro…", CentroDeA.Nombre);
    }

    /// <summary>El Centro elegido era del Cliente empresarial anterior: cambiarlo en el formulario lo quita.</summary>
    [Fact]
    public async Task Cambiar_el_Cliente_empresarial_del_alta_descarta_el_Centro_elegido()
    {
        _mediator.CentrosClienteB = [CentroDeB];
        var cut = await AbrirNuevoProyectoAsync();
        await SelectDelAlta(cut, "Centro").ChangeAsync(new ChangeEventArgs { Value = CentroDeA.Id.ToString() });

        await SelectDelAlta(cut, "Cliente").ChangeAsync(new ChangeEventArgs { Value = ClienteBId.ToString() });
        await EscribirNombreDelProyectoAsync(cut, "Montaje cámaras 2026");
        await BotonConTexto(cut, ".drawer-panel button", "Crear proyecto").ClickAsync(new MouseEventArgs());

        _mediator.Enviados.OfType<CrearProyectoCommand>().Should().BeEmpty("un Centro de A bajo el Cliente empresarial B no se envía");
        cut.Find(".drawer-panel [role=alert]").TextContent.Trim().Should().Be("Selecciona un centro.");
    }
}
