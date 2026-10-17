using Bunit;
using CaeManager.Application.Visitas.Commands.EditarContactoVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Visitas.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// Contacto de la Visita y recomendaciones en Visita 360 (decisión de producto, 2026-10-16). El contacto es un dato
/// personal: solo se pinta con <c>ContactoVisible</c>, que decide la consulta. Las recomendaciones solo se pintan si
/// salen de la documentación cargada; sin ninguna, no hay sección.
/// </summary>
public partial class Visita360PaginaTests
{
    private static Application.Visitas.Queries.ObtenerDetalleVisita.DetalleVisitaDto DetalleConContacto(
        string? nombre = "Pedro Salas", string? telefono = "954 555 010", string? correo = "pedro@centro.example") =>
        Detalle(porCorreo: false) with { ContactoVisible = true, ContactoNombre = nombre, ContactoTelefono = telefono, ContactoCorreo = correo };

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void El_contacto_de_la_visita_va_en_el_lateral_y_solo_escritura_lo_edita(string rol, bool puedeEditar)
    {
        var (cut, _) = Montar(DetalleConContacto(), rol);

        cut.Find("[data-pieza=lateral]").QuerySelectorAll(".tarjeta-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Información", "Contacto en el Centro", "Nota interna");
        var caja = cut.Find(".visita360-contacto");
        caja.TextContent.Should().Contain("Pedro Salas").And.Contain("954 555 010").And.Contain("pedro@centro.example");
        caja.QuerySelectorAll("button").Should().HaveCount(puedeEditar ? 1 : 0);
        cut.FindComponents<EditarContactoVisitaDrawer>().Should().HaveCount(puedeEditar ? 1 : 0);
    }

    [Fact]
    public void Sin_permiso_para_ver_el_contacto_no_hay_caja_ni_datos_ni_Drawer_de_contacto()
    {
        var (cut, _) = Montar(Detalle(porCorreo: false) with { ContactoVisible = false });

        cut.FindAll(".visita360-contacto").Should().BeEmpty();
        cut.Markup.Should().NotContain("Contacto en el Centro");
    }

    [Fact]
    public void Una_visita_sin_contacto_lo_dice_y_ofrece_anadirlo_a_quien_escribe()
    {
        var (cut, _) = Montar(DetalleConContacto(null, null, null));

        var caja = cut.Find(".visita360-contacto");
        caja.TextContent.Should().Contain("Esta visita no tiene contacto.");
        caja.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().Equal("Añadir contacto");
    }

    [Fact]
    public async Task Cambiar_el_contacto_envia_la_version_leida_y_refresca_la_caja()
    {
        var detalle = DetalleConContacto();
        var (cut, mediador) = Montar(detalle);

        await cut.Find(".visita360-contacto button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarContactoVisitaDrawer>();
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse("el Drawer abre con el contacto actual");
        var nombre = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Valor == "Pedro Salas");
        await nombre.Find("input").InputAsync(new ChangeEventArgs { Value = "Lucía Marín" });
        await nombre.Find("input").BlurAsync(new FocusEventArgs());
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EditarContactoVisitaCommand>().Should().ContainSingle().Subject.Should().BeEquivalentTo(
            new EditarContactoVisitaCommand(detalle.Id, detalle.Version, "Lucía Marín", "954 555 010", "pedro@centro.example"));
        cut.Find(".visita360-contacto").TextContent.Should().Contain("Lucía Marín").And.NotContain("Pedro Salas");
    }

    [Fact]
    public void Las_recomendaciones_salen_de_la_documentacion_cargada()
    {
        var (cut, _) = Montar(Detalle(porCorreo: false), documentacion: DocumentacionConIncidencias());

        var caja = cut.Find(".visita360-caja-recomendaciones");
        caja.QuerySelectorAll("li").Select(li => li.TextContent.Trim())
            .Should().Equal("Paula Campos tiene documentación pendiente: Aptitud médica, Entrega de EPI.");
    }

    [Fact]
    public void Sin_nada_de_lo_que_salga_una_recomendacion_no_hay_seccion()
    {
        var alDia = new DocumentacionVisitaDto(
            Guid.NewGuid(),
            new(EstadoDocumento.Vigente, [Documento("RNT", EstadoDocumento.Vigente)]),
            [new(Paula, "Paula Campos", "Contratista Demo S.L.", new(EstadoDocumento.Vigente, [Documento("Formación PRL", EstadoDocumento.Vigente, Paula)]))]);

        var (cut, _) = Montar(Detalle(porCorreo: false), documentacion: alDia);

        cut.FindAll(".visita360-caja-recomendaciones").Should().BeEmpty();
        cut.Markup.Should().NotContain("Recomendaciones");
    }
}
