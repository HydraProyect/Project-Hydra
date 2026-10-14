using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class VehiculosEstadoDocumentalTests
{
    [Fact]
    public void Chevron_abre_y_cierra_una_fila_de_detalle_independiente()
    {
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo("Furgoneta", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -3)) } });
        cut.FindAll(".vehiculo-detalle-incidencias").Should().BeEmpty();
        var chevron = cut.Find(".boton-expandir-incidencias");
        chevron.GetAttribute("aria-expanded").Should().Be("false");
        chevron.Click();
        cut.FindAll(".vehiculo-detalle-incidencias").Should().ContainSingle().Which.ClassList.Should().NotContain("fila-pulsable");
        cut.Find(".boton-expandir-incidencias").GetAttribute("aria-expanded").Should().Be("true");
        cut.Find(".boton-expandir-incidencias").Click();
        cut.FindAll(".vehiculo-detalle-incidencias").Should().BeEmpty();
    }

    [Fact]
    public void Expandir_todo_solo_despliega_propietarios_con_incidencias_y_contraer_las_cierra()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Almacen = {
            Vehiculo("A", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -3)),
            Vehiculo("B", EstadoDocumento.SinConfirmar, IncidenciaDe("Seguro", EstadoDocumento.SinConfirmar)),
            Vehiculo("C") }
        });
        cut.FindAll(".boton-expandir-incidencias").Should().HaveCount(2);
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".vehiculo-detalle-incidencias").Should().HaveCount(2);
        cut.Find("button[data-keytip=E]").TextContent.Should().Contain("Contraer todo");
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".vehiculo-detalle-incidencias").Should().BeEmpty();
    }

    [Fact]
    public async Task Detalles_no_suman_al_pie_y_cambiar_pagina_limpia_la_expansion()
    {
        var mediador = new MediadorFalso();
        for (var i = 0; i < 21; i++) mediador.Almacen.Add(Vehiculo($"Vehículo {i:00}", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -2)));
        var cut = Renderizar(mediador);
        AbrirIncidencias(cut);
        cut.Find(".paginador-texto").TextContent.Should().Be("Mostrando 20 de 21");
        Filas(cut).Should().HaveCount(20);
        await cut.Find("button[aria-label='Siguiente']").ClickAsync(new MouseEventArgs());
        cut.Find(".paginador-texto").TextContent.Should().Be("Mostrando 1 de 21");
        cut.FindAll(".vehiculo-detalle-incidencias").Should().BeEmpty();
        await cut.Find("button[aria-label='Anterior']").ClickAsync(new MouseEventArgs());
        cut.FindAll(".vehiculo-detalle-incidencias").Should().BeEmpty();
    }

    [Fact]
    public async Task J_k_y_Enter_recorren_propietarios_sin_pasar_por_sus_documentos()
    {
        var a = Vehiculo("A", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -3));
        var b = Vehiculo("B", EstadoDocumento.SinConfirmar, IncidenciaDe("Seguro", EstadoDocumento.SinConfirmar));
        var cut = Renderizar(new MediadorFalso { Almacen = { a, b } });
        cut.Find("button[data-keytip=E]").Click();
        cut.FindAll(".vehiculo-detalle-incidencias").Should().HaveCount(2);
        var teclado = cut.FindComponent<AtajosListaTeclado>();
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("j"));
        cut.Find("tr.fila-enfocada").QuerySelector(".enlace-nombre-fila")!.TextContent.Should().Be("A");
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("j"));
        cut.Find("tr.fila-enfocada").QuerySelector(".enlace-nombre-fila")!.TextContent.Should().Be("B");
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("Enter"));
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual!.EntidadId.Should().Be(b.Id);
        await cut.InvokeAsync(() => teclado.Instance.OnAtajo.InvokeAsync("k"));
        cut.Find("tr.fila-enfocada").QuerySelector(".enlace-nombre-fila")!.TextContent.Should().Be("A");
    }

    [Fact]
    public void La_coincidencia_local_sin_acentos_abre_y_resalta_solo_datos_ya_devueltos()
    {
        // Este doble devuelve una página ya seleccionada: no demuestra búsqueda SQL por documento.
        var cut = Renderizar(new MediadorFalso { Almacen = { Vehiculo("Formación móvil", EstadoDocumento.Urgente, IncidenciaDe("Formación Art. 19", EstadoDocumento.Urgente, 5)) } }, "vehiculos?q=formacion");
        cut.FindAll(".vehiculo-detalle-incidencias").Should().ContainSingle();
        cut.Find(".coincidencia-incidencia .incidencia-tipo mark").TextContent.Should().Be("Formación");
    }
}
