using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class TrabajadoresListaGen2Tests
{
    [Fact]
    public void Chevron_abre_y_cierra_un_detalle_independiente_con_estado_motivo_y_fecha()
    {
        var incidencia = IncidenciaDe("Aptitud médica", EstadoDocumento.Vencido, -3);
        var cut = Renderizar(new MediatorFalso { Almacen = { ConDesglose(Trabajador("Ana", "Alonso", estado: EstadoDocumento.Vencido), 1, 0, incidencia) } });
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
        var detalle = AbrirIncidencias(cut);
        detalle.ClassList.Should().NotContain("fila-pulsable");
        detalle.QuerySelector(".incidencia-tipo")!.TextContent.Should().Be("Aptitud médica");
        detalle.QuerySelector(".badge")!.TextContent.Should().Be("Vencido");
        detalle.QuerySelector(".incidencia-motivo")!.TextContent.Should().Be("Hace 3 días");
        detalle.QuerySelector(".incidencia-fecha")!.TextContent.Should().Be(incidencia.FechaVencimiento!.Value.ToString("dd/MM/yyyy"));
        cut.Find(".boton-expandir-fila").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
    }

    [Fact]
    public void Expandir_todo_omite_filas_sin_incidencia_y_contraer_cierra_las_abiertas()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = {
            ConDesglose(Trabajador("Ana", "Alonso"), 1, 0, IncidenciaDe("Contrato", EstadoDocumento.Vencido, -3)),
            ConDesglose(Trabajador("Bea", "Moreno"), 1, 0, IncidenciaDe("Aptitud", EstadoDocumento.SinConfirmar)), Trabajador("Zoe", "Zamora") }
        });
        cut.FindAll(".boton-expandir-fila").Should().HaveCount(2);
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().HaveCount(2);
        cut.Find("button[data-keytip=E]").TextContent.Should().Contain("Contraer todo");
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task La_busqueda_resalta_pero_el_cierre_manual_y_contraer_todo_prevalecen()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { ConDesglose(Trabajador("Formación", "Alonso"), 1, 0, IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5)) } }, "trabajadores?q=formacion");
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().ContainSingle();
        cut.Find(".coincidencia-incidencia .incidencia-tipo mark").TextContent.Should().Be("Formación");
        cut.Find("button[data-keytip=E]").TextContent.Should().Contain("Contraer todo");
        cut.Find(".boton-expandir-fila").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
        cut.Find(".boton-expandir-fila").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().ContainSingle();
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Formación"));
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().ContainSingle("otra búsqueda limpia el cierre manual");
        await cut.InvokeAsync(() => CajaDeBusqueda(cut).Instance.ValorChanged.InvokeAsync("Formación Art"));
        // La consulta del doble sí filtra identidad: no se inventa búsqueda por documento.
        FilasDeDatos(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task Detalles_no_suman_al_pie_y_la_pagina_limpia_expansion()
    {
        var mediador = new MediatorFalso();
        for (var i = 0; i < 21; i++) mediador.Almacen.Add(ConDesglose(Trabajador($"Persona{i:00}", "Alonso"), 1, 0, IncidenciaDe("Contrato", EstadoDocumento.Vencido, -2)));
        var cut = Renderizar(mediador);
        AbrirIncidencias(cut);
        cut.Find(".paginador-texto").TextContent.Should().EndWith("Mostrando 20 de 21");
        FilasDeDatos(cut).Should().HaveCount(20);
        await cut.Find("button[aria-label='Siguiente']").ClickAsync(new MouseEventArgs());
        cut.Find(".paginador-texto").TextContent.Should().EndWith("Mostrando 1 de 21");
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
        await cut.Find("button[aria-label='Anterior']").ClickAsync(new MouseEventArgs());
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task J_k_y_Enter_solo_recorren_trabajadores_con_los_detalles_abiertos()
    {
        var ana = ConDesglose(Trabajador("Ana", "Alonso"), 1, 0, IncidenciaDe("Contrato", EstadoDocumento.Vencido, -2));
        var bea = ConDesglose(Trabajador("Bea", "Moreno"), 1, 0, IncidenciaDe("Aptitud", EstadoDocumento.Vencido, -1));
        var cut = Renderizar(new MediatorFalso { Almacen = { ana, bea } }, "trabajadores?orden=trabajador");
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().HaveCount(2);
        var teclado = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("j"));
        cut.Find(".tarjeta-fila-acordeon.fila-enfocada .enlace-nombre-fila").TextContent.Should().Be("Ana Alonso");
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("j"));
        cut.Find(".tarjeta-fila-acordeon.fila-enfocada .enlace-nombre-fila").TextContent.Should().Be("Bea Moreno");
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("Enter"));
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(bea.Dto.Id);
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("k"));
        cut.Find(".tarjeta-fila-acordeon.fila-enfocada .enlace-nombre-fila").TextContent.Should().Be("Ana Alonso");
    }

    [Fact]
    public async Task El_refresco_puntual_conserva_la_expansion_del_trabajador_por_Id()
    {
        var ana = ConDesglose(Trabajador("Ana", "Alonso"), 1, 0, IncidenciaDe("Contrato", EstadoDocumento.Vencido, -2));
        var mediador = new MediatorFalso { Almacen = { ana } };
        var cut = Renderizar(mediador);
        AbrirIncidencias(cut);
        mediador.Almacen[0] = ConDesglose(ana, 1, 0, IncidenciaDe("Aptitud médica", EstadoDocumento.SinConfirmar));
        await AvisarGuardadoAsync(cut, EntidadWorkspace.Trabajador, ana.Dto.Id);
        cut.FindAll(".tarjeta-fila-acordeon-contenido").Should().ContainSingle();
        cut.Find(".incidencia-tipo").TextContent.Should().Be("Aptitud médica");
    }
    [Fact]
    public void El_vacio_filtrado_y_el_pie_permanecen_dentro_de_la_tarjeta()
    {
        var cut = Renderizar(new MediatorFalso(), "trabajadores?q=Nadie");
        cut.FindAll(".tarjeta-listado .estado-vacio").Should().ContainSingle();
        cut.Find(".tarjeta-listado-pie-separado .paginador-texto").TextContent.Should().EndWith("Mostrando 0 de 0");
        FilasDeDatos(cut).Should().BeEmpty();
    }
    [Fact]
    public async Task La_consulta_inicial_espera_la_URL_aunque_el_catalogo_se_demore()
    {
        var pendiente = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mediador = new MediatorFalso { Almacen = { Trabajador("Javier", "Salas"), Trabajador("Ana", "Vega") } };
        mediador.Retener = q => q is CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles.ObtenerEmpleadoresDeTrabajadoresVisiblesQuery ? pendiente.Task : null;
        Registrar(mediador, "trabajadores?q=Salas");
        var cut = Render<CaeManager.Web.Features.Trabajadores.Pages.Trabajadores>();

        await cut.InvokeAsync(() => Task.CompletedTask);
        mediador.Enviadas.OfType<CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores.ObtenerTrabajadoresQuery>().Should().BeEmpty("no se consulta con filtros por defecto antes de leer la URL");
        cut.Find("table.trabajadores-rejilla-propia").GetAttribute("aria-busy").Should().Be("true");

        cut.Markup.Should().NotContain("/trabajadores/exportar.xlsx", "no se ofrece exportar con criterios todavía sin sincronizar");
        await cut.InvokeAsync(() => pendiente.SetResult(new CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles.EmpleadoresDeTrabajadoresDto([], [])));
        cut.WaitForAssertion(() => Columna(cut, 0).Should().Equal("Salas"));
        mediador.Enviadas.OfType<CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores.ObtenerTrabajadoresQuery>().Should().ContainSingle().Which.Busqueda.Should().Be("Salas");
    }
}
