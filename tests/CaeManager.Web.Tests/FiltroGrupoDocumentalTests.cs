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
