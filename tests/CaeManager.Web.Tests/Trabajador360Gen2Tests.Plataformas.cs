using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class Trabajador360Gen2Tests
{
    [Theory]
    [InlineData(true, null, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    public void La_cabecera_muestra_extranjero_cobrable_solo_si_es_visible_y_efectivo(bool visible, bool? marca, bool porDocumento, bool esperado)
    {
        var id = Guid.NewGuid();
        var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { ExtranjeroCobrableVisible = visible, MarcaExtranjeroCobrable = marca, ExtranjeroCobrablePorDocumento = porDocumento };
        var cut = Renderizar(id);
        cut.Markup.Contains("Extranjero (cobrable)").Should().Be(esperado);
    }

    [Fact]
    public void Plataformas_filtra_por_acreditacion_sin_confundirla_con_la_vigencia_del_documento()
    {
        var rechazado = Documento("Formación PRL", EstadoDocumento.Vigente) with
        {
            AcreditacionesEnElCentro = [new AcreditacionResumenDto(Guid.NewGuid(), "Nalanda", EstadoAcreditacion.Rechazada)]
        };
        var validado = Documento("Aptitud médica", EstadoDocumento.Vencido) with
        {
            AcreditacionesEnElCentro = [new AcreditacionResumenDto(Guid.NewGuid(), "Dokify", EstadoAcreditacion.Aceptada)]
        };
        var centro = Centro("Centro Norte", "Empresa de prueba", EstadoDocumento.Vencido, rechazado, validado);
        var cut = Render<PlataformasTrabajador>(p => p.Add(x => x.Centros, [centro]));

        cut.FindAll(".plataformas-fila").Should().ContainSingle();
        cut.Markup.Should().Contain("Formación PRL").And.NotContain("Aptitud médica");
        cut.Find("a").GetAttribute("href").Should().Be($"/centros/{centro.CentroId}");
        cut.FindAll("button")[1].Click();
        cut.FindAll(".plataformas-fila").Should().HaveCount(2);
        cut.Markup.Should().Contain("Aptitud médica").And.Contain("Validado");
        cut.FindAll("button")[0].Click();
        cut.FindAll(".plataformas-fila").Should().ContainSingle();
    }

    [Fact]
    public void Plataformas_se_presenta_con_el_texto_de_la_maqueta_del_20_10()
    {
        var cut = Render<PlataformasTrabajador>();
        cut.Find(".plataformas-filtros p").TextContent
            .Should().Be("Estado de su documentación en la plataforma CAE de cada Cliente, por Centro.");
    }

    [Fact]
    public void Plataformas_sin_datos_no_afirma_que_todo_esta_validado()
    {
        var cut = Render<PlataformasTrabajador>();
        cut.Markup.Should().Contain("No hay acreditaciones disponibles").And.NotContain("Todo lo exigido está validado");
    }

    [Fact]
    public void La_URL_de_plataformas_abre_la_pestana_de_la_ficha()
    {
        var id = Guid.NewGuid();
        ConTrabajador(id);
        var cut = RenderizarEn(id, "?pestana=plataformas");
        cut.FindComponent<PlataformasTrabajador>();
        cut.Find("[role=tab][aria-selected=true]").TextContent.Should().Contain("Plataformas");
    }
}
