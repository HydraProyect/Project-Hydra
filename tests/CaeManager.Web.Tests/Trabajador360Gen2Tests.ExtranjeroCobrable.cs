using Bunit;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Casilla «Trabajador extranjero (cobrable)» del drawer de la ficha (decisión de Chris, 2026-10-20): se precarga con la
/// marca o, sin ella, con lo que dice el documento; solo se envía si se cambió, y volver al valor del documento devuelve
/// la decisión al documento (marca null). Sin <c>ExtranjeroCobrableVisible</c> no se pinta ni se envía.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    [Fact]
    public async Task La_casilla_se_precarga_con_el_documento_y_desmarcarla_envia_la_marca()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { ExtranjeroCobrableVisible = true, ExtranjeroCobrablePorDocumento = true };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        var casilla = drawer.Find("[data-campo='extranjero-cobrable'] input");
        casilla.HasAttribute("checked").Should().BeTrue("su documento lo cuenta como extranjero");
        drawer.Find("[data-campo='extranjero-cobrable']").TextContent.Should().Contain("Trabajador extranjero (cobrable)");
        drawer.Find("[data-ayuda='extranjero-cobrable']").TextContent.Should().Contain("cuenta como extranjero");

        await casilla.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.ExtranjeroCobrable.Should().Be(new MarcaExtranjeroCobrableTrabajador(false));
    }

    [Fact]
    public async Task Volver_al_valor_del_documento_devuelve_la_decision_al_documento()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            ExtranjeroCobrableVisible = true, ExtranjeroCobrablePorDocumento = false, MarcaExtranjeroCobrable = true
        };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        var casilla = drawer.Find("[data-campo='extranjero-cobrable'] input");
        casilla.HasAttribute("checked").Should().BeTrue("la marca manda");
        await casilla.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = false });
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.ExtranjeroCobrable.Should().Be(new MarcaExtranjeroCobrableTrabajador(null));
    }

    [Fact]
    public async Task Sin_cambiar_la_casilla_la_orden_no_la_lleva()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { ExtranjeroCobrableVisible = true, MarcaExtranjeroCobrable = false };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        await CambiarCampoFicha(drawer, "Nacionalidad", "Colombiana");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.ExtranjeroCobrable.Should().BeNull();
    }

    [Fact]
    public async Task Sin_permiso_la_casilla_no_se_pinta_ni_se_envia()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        drawer.FindAll("[data-campo='extranjero-cobrable']").Should().BeEmpty();
        await CambiarCampoFicha(drawer, "Nacionalidad", "Colombiana");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.ExtranjeroCobrable.Should().BeNull();
    }
}
