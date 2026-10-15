using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionEmpresasDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionRequeridaDeCentro;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    [Fact]
    public async Task Documentacion_del_Centro_lee_solo_al_abrir_y_presenta_las_filas_del_peor_al_mejor()
    {
        var id = Guid.NewGuid();
        var empresa = Guid.NewGuid();
        var vencido = Guid.NewGuid();
        var vigente = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.DocumentacionEmpresas[id] = [new(empresa, "Empresa Sur", false,
            [new(vencido, "Seguro", Guid.NewGuid(), EstadoDocumento.Vencido, new(2026, 10, 1), true, new(2025, 10, 1)),
             new(vigente, "Certificado", Guid.NewGuid(), EstadoDocumento.Vigente, new(2027, 1, 1), false)])];
        var navegador = Services.GetRequiredService<NavigationManager>();
        navegador.NavigateTo($"centros/{id}?q=Ana&estado=Vencido&extra=conservar");
        var cut = Renderizar(id);
        mediador.Enviadas.OfType<ObtenerDocumentacionEmpresasDeCentroQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentacionRequeridaDeCentroQuery>().Should().BeEmpty();
        await cut.Find("[role=tab][aria-controls$='-documentacion-centro']").ClickAsync(new MouseEventArgs());
        navegador.Uri.Should().Contain("q=Ana").And.Contain("estado=Vencido").And.Contain("extra=conservar");
        cut.Find(".barra-filtros").HasAttribute("hidden").Should().BeTrue();
        cut.FindComponents<FilaRelacion>().Select(r => r.Instance.Nombre).Should().Equal("Seguro", "Certificado");
        cut.Markup.Should().Contain("Mostrando 2 de 2").And.Contain("del peor estado al mejor");
        var filas = cut.FindComponents<FilaRelacion>();
        filas[0].Instance.Tono.Should().Be(TonoFila.Peligro);
        filas[1].Instance.Tono.Should().BeNull();
        filas[0].FindComponent<Badge>().Markup.Should().Contain("Vencido");
        cut.Markup.Should().Contain("Emitido el 01/10/2025").And.Contain("Vencimiento: 01/10/2026");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Empresa Sur").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Empresa, empresa, "Empresa Sur", "documentacion"));
        mediador.Enviadas.OfType<ObtenerDocumentacionEmpresasDeCentroQuery>().Should().ContainSingle().Which.CentroId.Should().Be(id);
        var consultas = mediador.Enviadas.Count;
        var franja = cut.FindComponents<FranjaEstado>().Single(c => c.Instance.EtiquetaAccesible == "Filtrar documentación del Centro por estado");
        franja.Instance.Opciones.Select(o => o.Texto).Should().Equal("Vencido", "Pendiente", "En tolerancia", "Por vencer", "Sin confirmar", "Vigente");
        await cut.InvokeAsync(() => franja.Instance.ValorChanged.InvokeAsync("Ficha360Vencido"));
        cut.FindComponents<FilaRelacion>().Should().ContainSingle().Which.Instance.Nombre.Should().Be("Seguro");
        cut.Markup.Should().Contain("Mostrando 1 de 2");
        var buscar = cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Buscar documento o Empresa empleadora");
        await cut.InvokeAsync(() => buscar.Instance.ValorChanged.InvokeAsync("No existe"));
        cut.FindComponents<FilaRelacion>().Should().BeEmpty();
        cut.Markup.Should().Contain("Ningún documento coincide con los filtros").And.Contain("Mostrando 0 de 2");
        mediador.Enviadas.Count.Should().Be(consultas);
        navegador.Uri.Should().Contain("q=Ana").And.Contain("estado=Vencido").And.Contain("extra=conservar");
    }

    [Fact]
    public void Requisitos_presenta_ambito_bloqueo_y_tolerancia_heredada_o_personalizada()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.Requisitos[id] = [new(Guid.NewGuid(), "Formación", AmbitoAplicacion.Trabajador, true, true, true, null, true, null, null, null, 5),
            new(Guid.NewGuid(), "Seguro", AmbitoAplicacion.Empresa, true, true, true, null, false, null, null, 0, 5),
            new(Guid.NewGuid(), "No aplicable", AmbitoAplicacion.Empresa, true, false, false, null, false, null, null)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=requisitos");
        var cut = Renderizar(id);
        cut.FindComponents<FilaRelacion>().Should().HaveCount(2);
        var filas = cut.FindComponents<FilaRelacion>();
        filas.Single(f => f.Instance.Nombre == "Formación").Find(".fila-relacion-estado").TextContent.Should().Contain("Bloquea el acceso");
        filas.Single(f => f.Instance.Nombre == "Seguro").Find(".fila-relacion-estado").TextContent.Should().Contain("No bloquea el acceso");
        cut.Markup.Should().Contain("Bloquea el acceso").And.Contain("No bloquea el acceso").And.Contain("Tolerancia: 5 días").And.Contain("Tolerancia: 0 días").And.NotContain("No aplicable");
    }

    [Fact]
    public void Contrato_es_un_dato_existente_que_basta_para_ofrecer_Ficha()
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Detalles[id] = Detalle(id, "Centro") with { CodigoCentro = null, Direccion = null, Contacto = null, ContratoVigenteHasta = new(2027, 3, 14) };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=ficha");
        var cut = Renderizar(id);
        cut.Find("[data-caja=datos-centro]").TextContent.Should().Contain("Contrato vigente hasta").And.Contain("14/03/2027");
        cut.FindComponents<DatoRejilla>().Should().ContainSingle();
    }

    [Theory]
    [InlineData("documentacion-centro", "Sin Empresas visibles en este Centro")]
    [InlineData("requisitos", "Sin requisitos documentales aplicables")]
    public void Lecturas_nuevas_tienen_estado_vacio_real(string pestana, string texto)
    {
        var id = Guid.NewGuid();
        Registrar(new MediatorFalso()).Detalles[id] = Detalle(id, "Centro");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana={pestana}");
        var cut = Renderizar(id);
        cut.Markup.Should().Contain(texto);
        cut.FindComponents<FilaRelacion>().Should().BeEmpty();
    }

    [Fact]
    public async Task Documentacion_del_Centro_anterior_no_pasa_al_siguiente_cuando_llega_tarde()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var pendiente = new TaskCompletionSource();
        var mediador = Registrar(new MediatorFalso { Retener = p => p is ObtenerDocumentacionEmpresasDeCentroQuery q && q.CentroId == a ? pendiente.Task : null });
        mediador.Detalles[a] = Detalle(a, "Centro A");
        mediador.Detalles[b] = Detalle(b, "Centro B");
        mediador.DocumentacionEmpresas[a] = [new(Guid.NewGuid(), "Empresa antigua", false, [])];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{a}?pestana=documentacion-centro");
        var cut = Renderizar(a);
        cut.Render(p => p.Add(x => x.CentroId, b));
        await cut.InvokeAsync(() => pendiente.SetResult());
        cut.Markup.Should().Contain("Centro B").And.NotContain("Empresa antigua");
    }
}
