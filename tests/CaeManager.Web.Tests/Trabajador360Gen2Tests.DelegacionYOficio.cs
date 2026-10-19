using Bunit;
using CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// La Delegación y el Oficio en el editor de la ficha del Trabajador: solo viajan si cambian (sin cambio, el servidor no
/// exige gestionar la Empresa), el Trabajador de Subcontrata sigue con el Puesto en texto, y un catálogo que no carga
/// deja los campos deshabilitados sin tocar lo que el Trabajador ya tiene.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    private static readonly Guid OficioSoldador = Guid.NewGuid();
    private static readonly Guid DelegacionSevilla = Guid.NewGuid();

    private static CatalogoTrabajadoresEmpresaDto CatalogoDePrueba() => new(
        [new DelegacionEmpresaDto(DelegacionSevilla, "Sevilla", null, null, null, Guid.NewGuid())],
        [new OficioEmpresaDto(OficioSoldador, "Soldador", Guid.NewGuid())]);

    private static EditarFichaTrabajadorCommand UnicaFichaEnviada(MediatorFalso m) =>
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;

    [Fact]
    public async Task Elegir_un_oficio_en_la_ficha_envia_los_ids_nuevos()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.CatalogoEmpresa = CatalogoDePrueba();
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        drawer.WaitForAssertion(() => drawer.FindAll("option").Select(o => o.TextContent).Should().Contain("Sevilla"));

        await drawer.Find("input[role=combobox]").InputAsync(new ChangeEventArgs { Value = "Sold" });
        await drawer.Find("li.selector-entidad-opcion").ClickAsync(new MouseEventArgs());
        await drawer.FindAll("select").Single(s => s.QuerySelectorAll("option").Any(o => o.TextContent == "Sevilla"))
            .ChangeAsync(new ChangeEventArgs { Value = DelegacionSevilla.ToString() });
        await GuardarFicha(drawer);

        UnicaFichaEnviada(m).DelegacionEmpresaYOficio.Should().Be(new DelegacionEmpresaYOficioDeTrabajador(DelegacionSevilla, OficioSoldador));
    }

    [Fact]
    public async Task Borrar_el_oficio_a_mano_lo_quita_y_sin_oficio_se_ve_el_puesto_actual()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.CatalogoEmpresa = CatalogoDePrueba();
        m.Detalles[id] = m.Detalles[id] with { OficioEmpresaId = OficioSoldador, Puesto = "Soldador" };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        drawer.WaitForAssertion(() => drawer.Find("input[role=combobox]").GetAttribute("value").Should().Be("Soldador"));
        drawer.Markup.Should().NotContain("Puesto actual:");

        await drawer.Find("input[role=combobox]").InputAsync(new ChangeEventArgs { Value = "" });
        await drawer.Find("input[role=combobox]").BlurAsync(new FocusEventArgs());

        drawer.Markup.Should().Contain("Puesto actual: Soldador.");
        await GuardarFicha(drawer);
        UnicaFichaEnviada(m).DelegacionEmpresaYOficio.Should().Be(new DelegacionEmpresaYOficioDeTrabajador(null, null));
    }

    [Fact]
    public async Task El_trabajador_de_subcontrata_edita_el_puesto_en_texto_y_no_envia_delegacion_ni_oficio()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { EmpresaId = null, SubcontrataId = Guid.NewGuid(), Puesto = "Peón" };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        drawer.FindComponents<SelectorDelegacionEmpresaYOficio>().Should().BeEmpty();
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Puesto u oficio").Instance.Valor.Should().Be("Peón");
        await CambiarCampoFicha(drawer, "Puesto u oficio", "Encofrador");
        await GuardarFicha(drawer);

        var enviada = UnicaFichaEnviada(m);
        enviada.Puesto.Should().Be("Encofrador");
        enviada.DelegacionEmpresaYOficio.Should().BeNull();
        m.Enviadas.OfType<ObtenerCatalogoTrabajadoresEmpresaQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Si_el_catalogo_no_carga_los_campos_quedan_deshabilitados_y_el_oficio_se_conserva()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.CatalogoEmpresa = null;
        m.Detalles[id] = m.Detalles[id] with { OficioEmpresaId = OficioSoldador, Puesto = "Soldador" };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);

        drawer.WaitForAssertion(() => drawer.Markup.Should().Contain("No se pudo cargar el catálogo de la empresa."));
        drawer.Find("input[role=combobox]").HasAttribute("disabled").Should().BeTrue();
        await drawer.Find("input[role=combobox]").BlurAsync(new FocusEventArgs());
        await GuardarFicha(drawer);

        UnicaFichaEnviada(m).DelegacionEmpresaYOficio.Should().BeNull("el Oficio que ya tenía no se toca");
    }
}
