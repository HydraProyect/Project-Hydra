using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Fact]
    public void La_cabecera_usa_la_fraccion_cargada_completa_sin_reconstruirla_desde_los_documentos_o_el_porcentaje()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Resumenes[id] = Resumen(id, "Centro Norte", porcentaje: 17);
        mediador.Asignaciones[id] =
        [
            Asignado("Ana Alba", EstadoDocumento.Vigente) with { Cumplimiento = new(4, 4) },
            Asignado("Zoe Zulu", EstadoDocumento.Vencido) with { Cumplimiento = new(1, 6) }
        ];
        var cut = Renderizar(id);
        var anillo = cut.FindComponent<AnilloCumplimiento>();
        anillo.Instance.Detalle.Should().Be("5 de 10");
        anillo.Instance.Porcentaje.Should().Be(50);
        cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("2 trabajadores asignados");
        var consultas = mediador.Enviadas.Count;
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"centros/{id}?q=Ana&estado=Vigente");
        cut.FindComponent<AnilloCumplimiento>().Instance.Detalle.Should().Be("5 de 10");
        cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("2 trabajadores asignados");
        mediador.Enviadas.Count.Should().Be(consultas);
    }

    [Fact]
    public async Task Una_respuesta_retardada_del_Centro_anterior_no_cambia_la_fraccion_del_nuevo()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var retenida = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso
        {
            Retener = peticion => peticion is ObtenerAsignacionesDocumentacionPorCentroQuery q && q.CentroId == a ? retenida.Task : null
        });
        mediador.Detalles[a] = Detalle(a, "Centro A");
        mediador.Detalles[b] = Detalle(b, "Centro B");
        mediador.Asignaciones[a] = [Asignado("Ana Alba", EstadoDocumento.Vigente) with { Cumplimiento = new(100, 100) }];
        mediador.Asignaciones[b] = [Asignado("Zoe Zulu", EstadoDocumento.Vencido) with { Cumplimiento = new(1, 3) }];
        var cut = Renderizar(a);
        cut.Render(p => p.Add(x => x.CentroId, b));
        cut.WaitForAssertion(() => cut.FindComponent<AnilloCumplimiento>().Instance.Detalle.Should().Be("1 de 3"));
        await cut.InvokeAsync(retenida.SetResult);
        cut.FindComponent<AnilloCumplimiento>().Instance.Detalle.Should().Be("1 de 3");
    }

    [Fact]
    public void La_franja_y_el_pie_no_se_ofrecen_en_el_acordeon_del_listado_sin_optar_por_la_lista_completa()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] = [Asignado("Ana Alba", EstadoDocumento.Vencido)];
        var cut = Render<AcordeonAsignacionesCentro>(p => p.Add(x => x.CentroId, id).Add(x => x.CentroNombre, "Centro Norte"));
        cut.FindComponents<FranjaEstado>().Should().BeEmpty();
        cut.FindAll("[data-totales-documentales]").Should().BeEmpty();
    }

    [Fact]
    public void La_pestana_de_Visitas_cuenta_las_visitas_cargadas()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Norte");
        mediador.Visitas[id] =
        [
            new(Guid.NewGuid(), new(2026, 12, 1), new(2026, 12, 2), 3),
            new(Guid.NewGuid(), new(2026, 12, 5), new(2026, 12, 6), 1)
        ];
        var cut = Renderizar(id);
        cut.FindAll("[role=tab]")[1].QuerySelector(".pestanas-contador")!.TextContent.Trim().Should().Be("2 visitas programadas");
    }

    [Fact]
    public void El_pie_conserva_el_universo_cero_sin_inventar_documentos()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] = [Asignado("Ana Alba", EstadoDocumento.Vigente)];
        var cut = RenderizarAcordeon(id);
        var pie = cut.Find("[data-totales-documentales]").TextContent;
        pie.Should().Contain("0 exigidos").And.Contain("0 al día").And.Contain("0 vencidos").And.Contain("0 pendientes");
    }

    [Theory]
    [InlineData(EstadoDocumento.Faltante, 1)]
    [InlineData(EstadoDocumento.SinConfirmar, 1)]
    [InlineData(EstadoDocumento.Vigente, 0)]
    [InlineData(EstadoDocumento.SinCaducidad, 0)]
    public void La_fila_ofrece_Pedir_solo_si_hay_documentacion_que_el_flujo_puede_pedir(EstadoDocumento estado, int acciones)
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Asignaciones[id] = [Asignado("Ana Alba", estado, Documento("Aptitud médica", estado))];
        var cut = RenderizarAcordeon(id);
        cut.FindAll(".fila-relacion-acciones button:not(.boton-360)").Should().HaveCount(acciones);
        if (acciones > 0)
            cut.Find(".fila-relacion-acciones button:not(.boton-360)").TextContent.Trim().Should().Be("Pedir");
    }
}
