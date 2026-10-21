using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Components;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

public partial class Trabajador360Gen2Tests
{
    private static async Task CambiarCampoFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer, string etiqueta, string valor)
    {
        var id = drawer.FindAll("label").Single(l => l.TextContent.Trim() == etiqueta).GetAttribute("for");
        await drawer.Find($"#{id}").InputAsync(new ChangeEventArgs { Value = valor });
        await drawer.Find($"#{id}").BlurAsync(new FocusEventArgs());
    }

    private static async Task GuardarFicha(IRenderedComponent<EditarFichaTrabajadorDrawer> drawer) =>
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

    private static async Task<IRenderedComponent<EditarFichaTrabajadorDrawer>> AbrirEditorFicha(IRenderedComponent<TrabajadorDetalle> cut)
    {
        await AbrirPestanaAsync(cut, "Ficha");
        await cut.Find("[data-caja='altas-plataforma'] button").ClickAsync(new MouseEventArgs());
        return cut.FindComponent<EditarFichaTrabajadorDrawer>();
    }

    [Fact]
    public async Task El_drawer_envia_nombre_documento_y_marca_en_una_sola_orden()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { ExtranjeroCobrableVisible = true };
        var drawer = await AbrirEditorFicha(Renderizar(id));
        await CambiarCampoFicha(drawer, "Nombre", "Marta");
        await CambiarCampoFicha(drawer, "Apellidos", "Salas");
        await CambiarCampoFicha(drawer, "Documento de identidad", "AB123456");
        drawer.Find("[data-campo='extranjero-cobrable'] input").HasAttribute("checked").Should().BeTrue();
        await drawer.Find("[data-campo='extranjero-cobrable'] input").ChangeAsync(new ChangeEventArgs { Value = false });
        await GuardarFicha(drawer);
        var c = m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;
        c.Nombre.Should().Be("Marta"); c.Apellidos.Should().Be("Salas"); c.Documento.Should().Be("AB123456");
        c.ExtranjeroCobrable.Should().Be(new MarcaExtranjeroCobrableTrabajador(false));
        m.Enviadas.OfType<CaeManager.Application.Trabajadores.Commands.CorregirDocumentoIdentidadTrabajador.CorregirDocumentoIdentidadTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Volver_al_documento_cargado_no_envia_una_marca_a_mano_que_nadie_toco()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            ExtranjeroCobrableVisible = true,
            Dni = "12345678Z",
            ExtranjeroCobrablePorDocumento = false,
            MarcaExtranjeroCobrable = true
        };
        var drawer = await AbrirEditorFicha(Renderizar(id));
        await CambiarCampoFicha(drawer, "Documento de identidad", "AB123456");
        await CambiarCampoFicha(drawer, "Documento de identidad", "12345678Z");
        drawer.Find("[data-campo='extranjero-cobrable'] input").HasAttribute("checked").Should().BeTrue();
        await GuardarFicha(drawer);
        var c = m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;
        c.Documento.Should().BeNull();
        c.ExtranjeroCobrable.Should().BeNull();
    }

    [Fact]
    public async Task Las_tres_cajas_tienen_los_rotulos_y_valores_exactos_de_la_maqueta()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            NumeroAfiliacionSeguridadSocial = "41123456789",
            Puesto = "Oficial 1.ª electricista",
            FechaNacimiento = new(1988, 3, 14),
            Nacionalidad = "Colombiana",
            TipoVinculo = "Plantilla",
            FechaAltaEmpresa = new(2023, 5, 2),
            FiguraEnRnt = "Sí, desde mayo de 2023",
            TipoPermisoTrabajo = "Residencia y trabajo por cuenta ajena",
            CaducidadPermisoTrabajo = new(2027, 2, 18),
            MotivoPermisoTrabajo = "Se identifica con NIE",
            PermisoTrabajoVisible = true
        };
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("altas-plataforma", "vinculo-empresa", "permiso-trabajo", "agenda-empleador");
        cut.FindAll("[data-caja='altas-plataforma'] dt").Select(d => d.TextContent.Trim()).Should().Equal(
            "N.º de afiliación a la Seguridad Social", "Puesto u oficio", "Fecha de nacimiento", "Nacionalidad");
        cut.FindAll("[data-caja='vinculo-empresa'] dt").Select(d => d.TextContent.Trim()).Should().Equal(
            "Tipo de vínculo", "Alta en la empresa", "Baja", "Figura en el RNT");
        cut.FindAll("[data-caja='permiso-trabajo'] dt").Select(d => d.TextContent.Trim()).Should().Equal("Tipo", "Caduca", "Motivo");
        cut.Find(".cajas-ficha").TextContent.Should().Contain("41 1234567 89").And.Contain("Colombiana")
            .And.Contain("02/05/2023").And.Contain("Sí, desde mayo de 2023").And.Contain("18/02/2027").And.Contain("Se identifica con NIE");
        cut.FindAll("[data-caja='vinculo-empresa'] dd")[2].TextContent.Trim().Should().Be("Sigue de alta", "la maqueta lo rotula así cuando hay alta y no baja");
    }

    // Decisión del 2026-10-15: al Usuario de Cliente el servidor no le manda el permiso de trabajo
    // (PermisoTrabajoVisible = false); la pantalla retira la caja entera en vez de pintarla vacía.
    [Fact]
    public async Task Sin_permiso_de_trabajo_visible_la_ficha_no_pinta_su_caja_y_conserva_afiliacion_y_vinculo()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with
        {
            NumeroAfiliacionSeguridadSocial = "41123456789",
            TipoVinculo = "Plantilla",
            PermisoTrabajoVisible = false
        };
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        cut.FindComponent<CajasFicha>().Instance.Cajas.Select(c => c.Clave).Should().Equal("altas-plataforma", "vinculo-empresa", "agenda-empleador");
        cut.FindAll("[data-caja='permiso-trabajo']").Should().BeEmpty();
        cut.Find(".cajas-ficha").TextContent.Should().Contain("41 1234567 89").And.Contain("Plantilla");
    }

    [Fact]
    public async Task Precarga_envia_los_once_campos_con_su_version_y_refresca_tras_guardar()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id); var version = m.Detalles[id].Version;
        m.Detalles[id] = m.Detalles[id] with { Nacionalidad = "Colombiana", FiguraEnRnt = "Sí, desde mayo de 2023" };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        drawer.FindComponents<CampoTexto>().Should().HaveCount(13);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Colombiana");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse();
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "41 1234567 89");
        await CambiarCampoFicha(drawer, "Fecha de nacimiento", "1988-03-14");
        await CambiarCampoFicha(drawer, "Tipo de vínculo", "Plantilla");
        await CambiarCampoFicha(drawer, "Alta en la empresa", "2023-05-02");
        await CambiarCampoFicha(drawer, "Baja", "2026-10-15");
        await CambiarCampoFicha(drawer, "Tipo", "Residencia y trabajo por cuenta ajena");
        await CambiarCampoFicha(drawer, "Caduca", "2027-02-18");
        await CambiarCampoFicha(drawer, "Motivo", "Se identifica con NIE");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
        await GuardarFicha(drawer);
        var c = m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Subject;
        c.Id.Should().Be(id); c.Version.Should().Be(version); c.NumeroAfiliacionSeguridadSocial.Should().Be("41 1234567 89");
        c.Puesto.Should().Be("Oficial de 1.ª — montaje"); c.DelegacionEmpresaYOficio.Should().BeNull("sin cambiar la Delegación ni el Oficio no se envían"); c.FechaNacimiento.Should().Be(new DateOnly(1988, 3, 14));
        c.Nacionalidad.Should().Be("Colombiana"); c.TipoVinculo.Should().Be("Plantilla");
        c.FechaAltaEmpresa.Should().Be(new DateOnly(2023, 5, 2)); c.FechaBajaEmpresa.Should().Be(new DateOnly(2026, 10, 15));
        c.FiguraEnRnt.Should().Be("Sí, desde mayo de 2023"); c.TipoPermisoTrabajo.Should().Be("Residencia y trabajo por cuenta ajena");
        c.CaducidadPermisoTrabajo.Should().Be(new DateOnly(2027, 2, 18)); c.MotivoPermisoTrabajo.Should().Be("Se identifica con NIE");
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        cut.Find("[data-caja='altas-plataforma']").TextContent.Should().Contain("Oficial de 1.ª — montaje");
        m.Enviadas.OfType<ObtenerTrabajadorPorIdQuery>().Should().HaveCount(3, "carga de página, apertura y refresco");
    }

    // Decisión del 2026-10-15: el tipo de contratación se elige en la ficha (cuenta ajena, autónomo o voluntario).
    [Fact]
    public async Task El_selector_de_tipo_de_contratacion_precarga_el_valor_y_envia_el_elegido()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { TipoContratacion = TipoContratacion.Autonomo };
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        var selector = drawer.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Tipo de contratación");
        selector.Instance.Valor.Should().Be("Autonomo", "precarga el tipo guardado, no el valor por defecto");
        selector.FindAll("option").Select(o => $"{o.GetAttribute("value")}={o.TextContent.Trim()}")
            .Should().Equal("CuentaAjena=Cuenta ajena", "Autonomo=Autónomo", "Voluntario=Voluntario");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse();

        await selector.Find("select").ChangeAsync(new ChangeEventArgs { Value = "Voluntario" });
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue("cambiar el tipo es un cambio sin guardar");
        await GuardarFicha(drawer);

        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle()
            .Which.TipoContratacion.Should().Be(TipoContratacion.Voluntario);
    }

    [Fact]
    public async Task El_conflicto_conserva_el_borrador_y_reintenta_solo_con_la_version_original()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id); var version = m.Detalles[id].Version;
        m.ResultadoFicha = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona ha guardado cambios."));
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "Nacionalidad", "Colombiana");
        await GuardarFicha(drawer);
        drawer.Markup.Should().Contain("Otra persona ha guardado cambios.");
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle();
        await GuardarFicha(drawer);
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().HaveCount(2)
            .And.OnlyContain(c => c.Version == version && c.Nacionalidad == "Colombiana");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
    }

    [Fact]
    public async Task Afiliacion_invalida_se_muestra_en_el_campo_y_no_se_envia()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "N.º de afiliación a la Seguridad Social", "ABC");
        await GuardarFicha(drawer);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "N.º de afiliación a la Seguridad Social")
            .Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task La_baja_anterior_al_alta_se_senala_en_su_campo_sin_enviar()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await CambiarCampoFicha(drawer, "Alta en la empresa", "2023-05-02");
        await CambiarCampoFicha(drawer, "Baja", "2023-05-01");
        await GuardarFicha(drawer);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Baja")
            .Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Reabrir_el_drawer_recarga_la_version_y_los_datos_del_servidor()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); var drawer = await AbrirEditorFicha(cut);
        await drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        var version = Guid.NewGuid();
        m.Detalles[id] = m.Detalles[id] with { Version = version, Nacionalidad = "Portuguesa" };
        drawer = await AbrirEditorFicha(cut);
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Portuguesa");
        await GuardarFicha(drawer);
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.Version.Should().Be(version);
    }

    [Fact]
    public async Task La_ficha_no_disponible_no_permite_guardar()
    {
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        m.Detalles.Remove(id);
        await cut.Find("[data-caja='altas-plataforma'] button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaTrabajadorDrawer>();
        drawer.FindComponent<DrawerFormulario>().Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").HasAttribute("disabled").Should().BeTrue();
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Consulta_ve_las_cajas_vacias_sin_edicion_ni_drawer()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var id = Guid.NewGuid(); var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { Puesto = null };
        var cut = Renderizar(id); await AbrirPestanaAsync(cut, "Ficha");
        cut.FindAll("[data-caja] dd").Should().HaveCount(11).And.OnlyContain(d => d.TextContent.Trim() == "—");
        cut.FindAll("[data-caja] button").Should().BeEmpty();
        cut.FindComponents<EditarFichaTrabajadorDrawer>().Should().BeEmpty();
    }

    [Fact]
    public async Task Una_carga_tardia_de_otro_trabajador_no_sustituye_el_borrador_actual()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var m = ConTrabajador(a);
        m.Detalles[b] = Detalle(b, "Otra", "Persona") with { Nacionalidad = "Portuguesa" };
        var retenida = new TaskCompletionSource();
        m.Retener = q => q is ObtenerTrabajadorPorIdQuery { Id: var id } && id == a ? retenida.Task : null;
        var drawer = Render<EditarFichaTrabajadorDrawer>(p => p.Add(x => x.TrabajadorId, a).Add(x => x.Visible, true));
        await drawer.InvokeAsync(() => drawer.Render(p => p.Add(x => x.TrabajadorId, b)));
        await drawer.InvokeAsync(() => retenida.SetResult());
        drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nacionalidad").Instance.Valor.Should().Be("Portuguesa");
        await GuardarFicha(drawer);
        m.Enviadas.OfType<EditarFichaTrabajadorCommand>().Should().ContainSingle().Which.Id.Should().Be(b);
    }

    [Theory]
    [InlineData("41123456789", "41 1234567 89")]
    [InlineData("281234567840", "28 12345678 40")]
    [InlineData(null, null)]
    public void La_afiliacion_se_presenta_agrupada_como_en_la_maqueta(string? guardado, string? presentado) =>
        TrabajadorDetalle.FormatoAfiliacion(guardado).Should().Be(presentado);
}
