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
        var tabla = cut.Find(".centro-documentacion-tabla");
        tabla.QuerySelectorAll("[role=columnheader]").Select(c => c.TextContent.Trim())
            .Should().Equal("Documento", "Estado en la plataforma", "Caducidad", "Bloquea");
        var filas = cut.FindAll(".centro-documentacion-fila");
        filas.Should().HaveCount(2);
        filas.Select(f => f.QuerySelector(".centro-documentacion-nombre > span")!.TextContent.Trim())
            .Should().Equal("Seguro", "Certificado");
        filas[0].QuerySelector(".centro-documentacion-t")!.GetAttribute("title").Should().Be("En TALVEG: Vencido");
        filas[1].QuerySelector(".centro-documentacion-t")!.GetAttribute("title").Should().Be("En TALVEG: Vigente");
        filas[0].QuerySelector(".centro-documentacion-t")!.GetAttribute("data-estado").Should().Be("Peligro");
        filas[1].QuerySelector(".centro-documentacion-t")!.GetAttribute("data-estado").Should().Be("Exito");
        filas.Should().OnlyContain(f => f.QuerySelector(".centro-documentacion-plataforma")!.TextContent.Trim() == "Sin datos de plataforma");
        // El DTO entrega la regla de bloqueo, pero no el estado de acreditación: nunca se infiere «Sí».
        filas.Should().OnlyContain(f => f.QuerySelectorAll("[role=cell]")[3].TextContent.Trim() == "—");
        filas[0].QuerySelector(".centro-documentacion-fecha")!.TextContent.Should().Contain("01/10/2026");
        filas[0].QuerySelector(".texto-fecha-copiable")!.GetAttribute("data-copiar-alt").Should().Be("01/10/2025");
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Empresa Sur").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Empresa, empresa, "Empresa Sur", "documentacion"));
        mediador.Enviadas.OfType<ObtenerDocumentacionEmpresasDeCentroQuery>().Should().ContainSingle().Which.CentroId.Should().Be(id);
        navegador.Uri.Should().Contain("q=Ana").And.Contain("estado=Vencido").And.Contain("extra=conservar");
    }

    [Theory]
    [InlineData(EstadoAcreditacion.Rechazada, "Rechazado", "Sí", "centro-documentacion-bloquea")]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, "Pendiente de subir", "Sí", "centro-documentacion-bloquea")]
    [InlineData(EstadoAcreditacion.Subida, "Pendiente de validación", "No", "centro-documentacion-pendiente")]
    [InlineData(EstadoAcreditacion.Aceptada, "Validado", "No", "")]
    public void Documentacion_Empresa_separa_acreditacion_en_el_Centro_de_vigencia_en_TALVEG(
        EstadoAcreditacion estado, string texto, string bloquea, string clase)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro Sur");
        mediador.DocumentacionEmpresas[id] = [new(Guid.NewGuid(), "Empresa Sur", false,
            [new(Guid.NewGuid(), "Seguro", Guid.NewGuid(), EstadoDocumento.Vigente, new(2027, 1, 1), true,
                AcreditacionesEnElCentro: [new(Guid.NewGuid(), "Nalanda", estado)])])];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=documentacion-centro");
        var fila = Renderizar(id).Find(".centro-documentacion-fila");
        fila.QuerySelector(".centro-documentacion-plataforma")!.TextContent.Should().Contain(texto).And.Contain("Nalanda");
        fila.QuerySelectorAll("[role=cell]")[3].TextContent.Trim().Should().Be(bloquea);
        fila.QuerySelector(".centro-documentacion-t")!.GetAttribute("title").Should().Be("En TALVEG: Vigente");
        if (clase.Length > 0) fila.ClassList.Should().Contain(clase);
        else fila.ClassList.Should().NotContain("centro-documentacion-bloquea").And.NotContain("centro-documentacion-pendiente");
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
        cut.FindComponent<CajasFicha>().FindComponents<DatoRejilla>().Should().ContainSingle();
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
