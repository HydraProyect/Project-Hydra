using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Commands.EditarFichaEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Empresas.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Empresa360PaginaTests
{
    private async Task<IRenderedComponent<EditarFichaEmpresaDrawer>> AbrirFichaEmpresa()
    {
        var cut = Renderizar("?pestana=ficha");
        await cut.Find("[data-caja='firma-documentos'] button").ClickAsync(new MouseEventArgs());
        return cut.FindComponent<EditarFichaEmpresaDrawer>();
    }

    private static async Task GuardarFichaEmpresa(IRenderedComponent<EditarFichaEmpresaDrawer> drawer) =>
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

    [Fact]
    public void Las_cajas_de_la_ficha_muestran_los_datos_de_organizaciones_y_contactos_por_separado()
    {
        var m = Empresa();
        m.Detalles[EmpresaId] = m.Detalles[EmpresaId] with
        {
            CanalFirmaDocumentos = "DocuSign",
            SelloCustodiadoPorOperadorCae = true,
            FirmanteDocumentos = "Ana Firma",
            ModalidadOrganizacionPreventiva = "Servicio de prevencion ajeno",
            OrganizacionSpa = "Prevencion Norte",
            ContactoSpaNombre = "Maria SPA",
            ContactoSpaTelefono = "600111222",
            ContactoSpaCorreo = "spa@example.test",
            Mutua = "Mutua Norte",
            ContactoFormacionNombre = "Luis Formacion",
            ContactoFormacionTelefono = "600333444",
            ContactoFormacionCorreo = "formacion@example.test",
            TecnicoPrlNombre = "Eva PRL",
            TecnicoPrlTelefono = "600555666",
            TecnicoPrlCorreo = "prl@example.test"
        };
        Registrar(m);
        var cut = Renderizar("?pestana=ficha");
        cut.Find("[data-caja='firma-documentos']").TextContent.Should().Contain("Ana Firma").And.Contain("DocuSign");
        cut.Find("[data-caja='prevencion']").TextContent.Should().Contain("Prevencion Norte").And.Contain("Maria SPA")
            .And.Contain("600111222").And.Contain("spa@example.test");
        cut.Find("[data-caja='mutua']").TextContent.Should().Contain("Mutua Norte");
        cut.Find("[data-caja='formacion']").TextContent.Should().Contain("Luis Formacion").And.Contain("600333444")
            .And.Contain("formacion@example.test").And.NotContain("Maria SPA");
        cut.Find("[data-caja='prevencion']").TextContent.Should().Contain("Eva PRL").And.Contain("600555666").And.Contain("prl@example.test");
    }

    [Fact]
    public void Consulta_ve_las_cajas_vacias_sin_botones_de_edicion()
    {
        var m = Empresa(); m.Sellos.Clear(); Registrar(m, Roles.Consulta);
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Should().HaveCount(4);
        cut.FindAll("[data-caja] dd").Should().NotBeEmpty().And.OnlyContain(d => d.TextContent.Trim() == "\u2014");
        cut.FindAll("[data-caja] button").Should().BeEmpty();
        cut.FindComponents<EditarFichaEmpresaDrawer>().Should().BeEmpty();
    }

    [Fact]
    public void La_ficha_no_visible_no_muestra_datos_personales_ni_su_editor()
    {
        var m = Empresa(); m.Sellos.Clear();
        m.Detalles[EmpresaId] = m.Detalles[EmpresaId] with { FichaVisible = false, FirmanteDocumentos = "Dato reservado" };
        Registrar(m, Roles.Cliente);
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[data-caja='firma-documentos'],[data-caja='prevencion'],[data-caja='mutua'],[data-caja='formacion']").Should().BeEmpty();
        cut.Markup.Should().NotContain("Dato reservado");
        cut.FindComponents<EditarFichaEmpresaDrawer>().Should().BeEmpty();
    }

    [Fact]
    public async Task Precarga_los_contactos_y_envia_todos_los_campos_con_la_version_original()
    {
        var m = Empresa(); var version = m.Detalles[EmpresaId].Version;
        var detalle = m.Detalles[EmpresaId] with
        {
            CanalFirmaDocumentos = "DocuSign",
            SelloCustodiadoPorOperadorCae = true,
            FirmanteDocumentos = "Ana Firma",
            ModalidadOrganizacionPreventiva = "Servicio de prevencion ajeno",
            OrganizacionSpa = "Prevencion Norte",
            ContactoSpaNombre = "Maria SPA",
            ContactoSpaTelefono = "600111222",
            ContactoSpaCorreo = "spa@example.test",
            Mutua = "Mutua Norte",
            ContactoFormacionNombre = "Luis Formacion",
            ContactoFormacionTelefono = "600333444",
            ContactoFormacionCorreo = "formacion@example.test",
            TecnicoPrlNombre = "Eva PRL",
            TecnicoPrlTelefono = "600555666",
            TecnicoPrlCorreo = "prl@example.test"
        };
        m.Detalles[EmpresaId] = detalle; Registrar(m);
        var drawer = await AbrirFichaEmpresa();
        drawer.FindComponents<CampoTexto>().Select(c => c.Instance.Valor).Should().Contain("Ana Firma").And.Contain("Luis Formacion").And.Contain("Eva PRL");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse();
        await GuardarFichaEmpresa(drawer);
        var c = m.Enviadas.OfType<EditarFichaEmpresaCommand>().Should().ContainSingle().Subject;
        c.Should().BeEquivalentTo(new EditarFichaEmpresaCommand(EmpresaId, version, detalle.CanalFirmaDocumentos,
            detalle.SelloCustodiadoPorOperadorCae, detalle.FirmanteDocumentos, detalle.ModalidadOrganizacionPreventiva,
            detalle.OrganizacionSpa, detalle.ContactoSpaNombre, detalle.ContactoSpaTelefono, detalle.ContactoSpaCorreo,
            detalle.Mutua, detalle.ContactoFormacionNombre, detalle.ContactoFormacionTelefono, detalle.ContactoFormacionCorreo,
            detalle.TecnicoPrlNombre, detalle.TecnicoPrlTelefono, detalle.TecnicoPrlCorreo));
        m.Enviadas.OfType<ObtenerEmpresaPorIdQuery>().Should().HaveCount(3);
        drawer.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_conflicto_conserva_el_borrador_y_no_actualiza_su_version_en_el_reintento()
    {
        var m = Empresa(); var version = m.Detalles[EmpresaId].Version;
        m.Detalles[EmpresaId] = m.Detalles[EmpresaId] with { FirmanteDocumentos = "Ana Firma" };
        m.ResultadoFicha = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona ha guardado cambios."));
        Registrar(m); var drawer = await AbrirFichaEmpresa();
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Valor == "Ana Firma");
        await campo.Find("input").InputAsync(new ChangeEventArgs { Value = "Firma en borrador" });
        await campo.Find("input").BlurAsync(new FocusEventArgs());
        await GuardarFichaEmpresa(drawer);
        drawer.Markup.Should().Contain("Otra persona ha guardado cambios.");
        await GuardarFichaEmpresa(drawer);
        m.Enviadas.OfType<EditarFichaEmpresaCommand>().Should().HaveCount(2)
            .And.OnlyContain(c => c.Version == version && c.FirmanteDocumentos == "Firma en borrador");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
    }

    [Fact]
    public async Task El_correo_invalido_se_marca_en_su_campo_sin_enviar_el_comando()
    {
        var m = Empresa();
        m.Detalles[EmpresaId] = m.Detalles[EmpresaId] with { ContactoFormacionCorreo = "formacion@example.test" };
        Registrar(m); var drawer = await AbrirFichaEmpresa();
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Valor == "formacion@example.test");
        await campo.Find("input").InputAsync(new ChangeEventArgs { Value = "correo-invalido" });
        await campo.Find("input").BlurAsync(new FocusEventArgs());
        await GuardarFichaEmpresa(drawer);
        campo.Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        m.Enviadas.OfType<EditarFichaEmpresaCommand>().Should().BeEmpty();
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
    }

    [Fact]
    public async Task Guardar_refresca_las_cajas_con_el_detalle_devuelto_por_el_servidor()
    {
        var m = Empresa();
        m.Detalles[EmpresaId] = m.Detalles[EmpresaId] with { FirmanteDocumentos = "Ana Firma" };
        m.DetalleTrasGuardarFicha = m.Detalles[EmpresaId] with { FirmanteDocumentos = "Firma actualizada" };
        Registrar(m); var cut = Renderizar("?pestana=ficha");
        await cut.Find("[data-caja='firma-documentos'] button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaEmpresaDrawer>();
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Valor == "Ana Firma");
        await campo.Find("input").InputAsync(new ChangeEventArgs { Value = "Firma actualizada" });
        await campo.Find("input").BlurAsync(new FocusEventArgs());
        await GuardarFichaEmpresa(drawer);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.Find("[data-caja='firma-documentos']").TextContent.Should().Contain("Firma actualizada");
    }

    [Fact]
    public async Task Una_ficha_no_disponible_no_permite_guardar()
    {
        var m = Empresa(); Registrar(m);
        var cut = Renderizar("?pestana=ficha"); m.Detalles.Remove(EmpresaId);
        await cut.Find("[data-caja='firma-documentos'] button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaEmpresaDrawer>();
        drawer.FindComponent<DrawerFormulario>().Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").HasAttribute("disabled").Should().BeTrue();
        m.Enviadas.OfType<EditarFichaEmpresaCommand>().Should().BeEmpty();
    }
}
