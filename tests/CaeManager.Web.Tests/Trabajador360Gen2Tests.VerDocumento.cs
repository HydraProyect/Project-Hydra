using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Trabajador 360 › Operación (petición de Chris desde staging, 2026-11-09): cada documento que existe se puede abrir en el
/// visor sin salir de la ficha («Ver») y su nombre lleva a la ficha 360 del Documento (<c>/documentos/{id}</c>). Lo que
/// había en main —«Gestionar» para quien escribe y solo donde el documento pide trabajo— se conserva; lo que no tiene
/// Documento (un indicador que falta) no lleva ni enlace ni «Ver».
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private static readonly Guid BaseVencida = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid BaseVigente = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid PorVencerId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");
    private static readonly Guid SinConfirmarId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");

    private IRenderedComponent<Features.Trabajadores.Pages.TrabajadorDetalle> OperacionConDocumentos(bool conArchivo = true)
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        ComponentFactories.AddStub<VisorDocumento>();
        mediador.Detalles[id] = Detalle(id, "Javier", "Salas Moreno");
        mediador.BaseDocumental[id] = new([
            new(TipoDocumentoBase.AptitudMedica, EstadoIndicadorBase.Vencido, BaseVencida, null),
            new(TipoDocumentoBase.FormacionArt19, EstadoIndicadorBase.Falta, null, null),
            new(TipoDocumentoBase.InformacionArt18, EstadoIndicadorBase.Vigente, BaseVigente, null)]);
        var archivo = conArchivo ? "trabajadores/doc.pdf" : null;
        mediador.Documentos[id] =
        [
            DocumentoDelTrabajador("Reconocimiento médico", EstadoDocumento.Urgente, new(2025, 10, 1), new(2026, 10, 1), PorVencerId) with { ArchivoUrl = archivo },
            DocumentoDelTrabajador("Documento de identidad", EstadoDocumento.SinConfirmar, new(2026, 5, 31), null, SinConfirmarId) with { ArchivoUrl = archivo },
        ];
        return Renderizar(id);
    }

    private static List<string> TextosDeBotones(AngleSharp.Dom.IElement fila) =>
        fila.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();

    [Fact]
    public void Documentacion_base_enlaza_el_nombre_a_la_ficha_360_del_documento_y_lo_que_falta_no_lleva_enlace_ni_Ver()
    {
        var cut = OperacionConDocumentos();

        var filas = cut.FindAll(".panel-doc-base-fila");
        filas.Should().HaveCount(3);
        filas[0].QuerySelector(".panel-doc-base-nombre a")!.GetAttribute("href").Should().Be($"/documentos/{BaseVencida}");
        filas[2].QuerySelector(".panel-doc-base-nombre a")!.GetAttribute("href").Should().Be($"/documentos/{BaseVigente}");
        filas[1].QuerySelector(".panel-doc-base-nombre a").Should().BeNull("el documento que falta no existe: no hay ficha 360 a la que ir");
        TextosDeBotones(filas[0]).Should().Contain("Ver");
        TextosDeBotones(filas[2]).Should().Contain("Ver", "también el vigente se puede abrir");
        TextosDeBotones(filas[1]).Should().NotContain("Ver");
        filas[1].QuerySelector(".panel-doc-base-nombre")!.TextContent.Should().Contain("Formación Art. 19");
    }

    [Fact]
    public async Task Ver_en_documentacion_base_abre_el_visor_con_el_DocumentoId_de_su_fila_sin_salir_de_la_ficha()
    {
        var cut = OperacionConDocumentos();
        var antes = Navegacion.Uri;
        var visor = cut.FindComponent<Stub<VisorDocumento>>();
        visor.Instance.Parameters.Get(x => x.Visible).Should().BeFalse();

        var ver = cut.FindAll(".panel-doc-base-fila")[2].QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Ver");
        ver.GetAttribute("aria-label")!.Should().StartWith("Ver").And.Contain("Información Art. 18");
        await ver.ClickAsync(new MouseEventArgs());

        visor = cut.FindComponent<Stub<VisorDocumento>>();
        visor.Instance.Parameters.Get(x => x.Visible).Should().BeTrue();
        visor.Instance.Parameters.Get(x => x.DocumentoId).Should().Be(BaseVigente);
        Navegacion.Uri.Should().Be(antes, "«Ver» abre el visor, no navega");
    }

    [Fact]
    public void Gestionar_se_conserva_en_documentacion_base_junto_a_Ver_y_lo_vigente_no_lo_lleva()
    {
        var cut = OperacionConDocumentos();

        cut.FindAll(".panel-doc-base-fila").Select(TextosDeBotones).Should().Equal(
            new List<string>[] { ["Ver", "Gestionar"], ["Gestionar"], ["Ver"] },
            (a, b) => a.SequenceEqual(b));
    }

    [Fact]
    public void Por_vencer_y_Vigencia_sin_confirmar_enlazan_el_nombre_a_la_ficha_360_y_conservan_Gestionar()
    {
        var cut = OperacionConDocumentos();

        var porVencer = cut.Find(".trabajador360-por-vencer li.fila-relacion");
        porVencer.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href").Should().Be($"/documentos/{PorVencerId}");
        TextosDeBotones(porVencer).Should().Equal("Ver", "Gestionar");

        var sinConfirmar = cut.Find("[aria-label='Vigencia sin confirmar'] li.fila-relacion");
        sinConfirmar.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href").Should().Be($"/documentos/{SinConfirmarId}");
        TextosDeBotones(sinConfirmar).Should().Equal("Ver", "Gestionar");
    }

    [Theory]
    [InlineData("por-vencer")]
    [InlineData("sin-confirmar")]
    public async Task Ver_en_las_franjas_abre_el_visor_con_el_DocumentoId_del_documento_de_la_fila(string franja)
    {
        var cut = OperacionConDocumentos();
        var (selector, esperado) = franja == "por-vencer"
            ? (".trabajador360-por-vencer li.fila-relacion", PorVencerId)
            : ("[aria-label='Vigencia sin confirmar'] li.fila-relacion", SinConfirmarId);

        await cut.Find(selector).QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Ver").ClickAsync(new MouseEventArgs());

        var visor = cut.FindComponent<Stub<VisorDocumento>>();
        visor.Instance.Parameters.Get(x => x.Visible).Should().BeTrue();
        visor.Instance.Parameters.Get(x => x.DocumentoId).Should().Be(esperado);
        visor.Instance.Parameters.Get(x => x.Titulo).Should().Be(franja == "por-vencer" ? "Reconocimiento médico" : "Documento de identidad");
    }

    [Fact]
    public void Un_documento_de_las_franjas_sin_archivo_enlaza_a_su_ficha_pero_no_ofrece_Ver()
    {
        var cut = OperacionConDocumentos(conArchivo: false);

        var porVencer = cut.Find(".trabajador360-por-vencer li.fila-relacion");
        porVencer.QuerySelector("a.fila-relacion-nombre").Should().NotBeNull();
        TextosDeBotones(porVencer).Should().Equal("Gestionar");
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Consulta_y_Cliente_ven_y_abren_el_documento_pero_no_reciben_ninguna_accion_de_escritura(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = OperacionConDocumentos();

        cut.FindAll(".panel-doc-base-fila").Select(TextosDeBotones).Should().Equal(
            new List<string>[] { ["Ver"], [], ["Ver"] }, (a, b) => a.SequenceEqual(b));
        TextosDeBotones(cut.Find(".trabajador360-por-vencer li.fila-relacion")).Should().Equal("Ver");
        TextosDeBotones(cut.Find("[aria-label='Vigencia sin confirmar'] li.fila-relacion")).Should().Equal("Ver");
        cut.Markup.Should().NotContain("Gestionar");
        cut.FindAll(".panel-doc-base-nombre a").Should().HaveCount(2);
    }
}
