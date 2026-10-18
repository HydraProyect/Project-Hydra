using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public class FiltroGrupoDocumentalTests : BunitContext
{
    public FiltroGrupoDocumentalTests() { Services.AddLocalization(); JSInterop.Mode = JSRuntimeMode.Loose; }
    private IRenderedComponent<FiltroGrupoDocumental> Crear()
    {
        var cut = Render<FiltroGrupoDocumental>(p => p.Add(c => c.Opciones,
            new RecuentoGrupoDocumental[] {
                new(AmbitoAplicacion.Trabajador, "Formación PRL", 3, 1),
                new(AmbitoAplicacion.Trabajador, "Información y EPI", 2, 0),
                new(AmbitoAplicacion.Empresa, "Igualdad", 1, 1) }));
        cut.Render(p => p.Add(c => c.SeleccionChanged, (IReadOnlyCollection<string> valores) =>
            cut.Render(n => n.Add(c => c.Seleccion, valores))));
        return cut;
    }

    [Fact]
    public void Seleccion_multiple_recuento_y_quitar_seleccion()
    {
        var cut = Crear();
        cut.Find(".grupo-boton").Click();
        cut.FindAll("[role=menuitemcheckbox]")[0].Click();
        cut.FindAll("[role=menuitemcheckbox]")[1].Click();
        cut.Find(".grupo-contador").TextContent.Should().Be("2");
        cut.FindAll("[aria-checked=true]").Count.Should().Be(2);
        cut.FindAll(".grupo-acciones button")[1].Click();
        cut.Instance.Seleccion.Should().BeEmpty();
        cut.FindAll("[role=menuitemcheckbox]")[0].TextContent.Should().Contain("1 archivados");
    }

    [Fact]
    public void La_linea_de_recuento_distingue_ninguno_uno_y_varios_y_no_parte_las_acciones()
    {
        var cut = Crear();
        cut.Find(".grupo-boton").Click();
        cut.Find(".grupo-resumen").TextContent.Trim().Should().Be("Ninguno seleccionado");
        cut.FindAll("[role=menuitemcheckbox]")[0].Click();
        cut.Find(".grupo-resumen").TextContent.Trim().Should().Be("1 seleccionado");
        cut.FindAll("[role=menuitemcheckbox]")[1].Click();
        cut.Find(".grupo-resumen").TextContent.Trim().Should().Be("2 seleccionados");
        // El resumen es el primer hijo: es el que cede el sitio; las acciones son botones y el punto, que el CSS no parte.
        cut.Find(".grupo-acciones").FirstElementChild!.ClassList.Should().Contain("grupo-resumen");
    }

    [Fact]
    public void Buscar_ambito_y_acciones_solo_sobre_resultados_preservan_la_seleccion_oculta()
    {
        var cut = Crear();
        cut.Find(".grupo-boton").Click();
        cut.FindAll("[role=menuitemcheckbox]")[2].Click();
        cut.Find("input").Input("trabajador");
        cut.FindAll("[role=menuitemcheckbox]").Count.Should().Be(2);
        cut.FindAll(".grupo-acciones button")[0].Click();
        cut.Instance.Seleccion.Count.Should().Be(3);
        cut.FindAll(".grupo-acciones button")[1].Click();
        cut.Instance.Seleccion.Should().Equal("Igualdad");
        cut.Find("input").Input("formacionprl");
        cut.FindAll("[role=menuitemcheckbox]").Should().ContainSingle();
        cut.Find("input").Input("inexistente");
        cut.Find(".grupo-vacio").TextContent.Should().Contain("Ningún grupo coincide con «inexistente»");
        cut.Find("input").KeyDown("Escape");
        cut.FindAll("[role=menu]").Should().BeEmpty();
    }
}
