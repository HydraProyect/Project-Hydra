using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesDelDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-23 y D-24 (lienzo «Documentos · listado sin pestañas», artboards Vista-rapida y Vista-rapida-estados): la pestaña
/// Información de la vista rápida conserva lo que tenía main (datos, «Descargar PDF», «Renovar», «Reclamar») y gana «Ver» después
/// de «Descargar PDF» y la sección «Plataformas CAE». Por rol: el menú «Acreditación ▾» es de quien escribe (Administrador,
/// Gestor CAE); Consulta ve la sección con el motivo pero sin menú; el Usuario de Cliente ve el estado sin motivo ni menú, aunque
/// el motivo llegara (el servidor ya no lo manda: ObtenerAcreditacionesDelDocumentoQueryTests).
/// </summary>
public partial class DocumentoWorkspacePanelTests
{
    private static AcreditacionDelDocumentoDto Acreditacion(string plataforma, string clienteEmpresarial, string centro,
        EstadoAcreditacion estado, EstadoVigenciaEnPlataforma vigencia = EstadoVigenciaEnPlataforma.SinConfirmar,
        string? motivo = null, string? url = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), plataforma, plataforma.ToLowerInvariant(), clienteEmpresarial, centro, estado, vigencia, motivo, url);

    /// <summary>Las dos del artboard Vista-rapida: Nalanda rechazada con motivo y CTAIMA pendiente de subir.</summary>
    private static AcreditacionesDelDocumentoDto DosAcreditaciones() => new(EstadoDocumento.Vencido,
    [
        Acreditacion("Nalanda", "Refrigeración Alcor S.A.", "Centro Norte", EstadoAcreditacion.Rechazada,
            motivo: "Documento ilegible, el escaneo está cortado por el borde", url: "https://nalanda.example/acceso"),
        Acreditacion("CTAIMA", "Aceros Lasarte S.A.", "Nave Berriz", EstadoAcreditacion.PendienteDeSubir),
    ]);

    private IRenderedComponent<DocumentoWorkspacePanel> RenderInformacion(DocumentoDetalleDto detalle, AcreditacionesDelDocumentoDto? acreditaciones,
        bool fallaAcreditaciones = false)
    {
        Services.AddScoped<ToastService>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        Services.AddScoped<IMediator>(_ => new MediatorDocumento { Detalle = detalle, Acreditaciones = acreditaciones, FallaAcreditaciones = fallaAcreditaciones });
        return Render<DocumentoWorkspacePanel>(p => p
            .Add(x => x.EntidadId, detalle.Id)
            .Add(x => x.PestanaActiva, "informacion"));
    }

    // ------------------------------------------------------------- lo que main ya tenía

    [Fact]
    public void Informacion_conserva_los_datos_y_las_acciones_de_main()
    {
        var detalle = Detalle() with { ArchivoUrl = "documentos/certificado.pdf", Comentarios = "Pedido el 28/09" };
        var cut = RenderInformacion(detalle, null);

        cut.FindAll(".celda-info-documento-360 > span:first-child").Select(s => s.TextContent.Trim()).Should().Equal(
            "Propietario", "Tipo de documento", "Fecha de emisión", "Fecha de vencimiento", "Comentarios", "Archivo");
        cut.Find(".acciones-documento-360").TextContent.Should().Contain("Renovar").And.Contain("Reclamar");
        cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).Should().Equal("Información", "Validación", "Firma", "Historial");
        cut.Find("a[href='/documentos/" + detalle.Id + "']").Should().NotBeNull("el enlace a Documento 360 sigue en la cabecera");
    }

    // ------------------------------------------------------------- D-24: «Descargar PDF» y «Ver»

    [Fact]
    public void Archivo_ofrece_Descargar_PDF_primero_y_Ver_despues_y_Ver_abre_el_visor_del_listado()
    {
        var detalle = Detalle() with { ArchivoUrl = "documentos/certificado.pdf" };
        var cut = RenderInformacion(detalle, null);

        var archivo = cut.Find(".archivo-documento-360");
        archivo.Children.Select(c => c.TextContent.Trim()).Should().Equal(["Descargar PDF", "Ver"], "D-24: «Descargar PDF» primero");
        archivo.Children[0].GetAttribute("href").Should().Be($"/documentos/{detalle.Id}/archivo");
        archivo.Children[0].HasAttribute("download").Should().BeTrue();
        archivo.Children[1].GetAttribute("aria-label").Should().Contain("Certificado TGSS").And.Contain("Refrielectric S.A.");
        cut.FindComponents<VisorDocumento>().Should().BeEmpty("el visor solo se monta mientras está abierto");

        cut.Find("[data-accion=ver-pdf]").Click();

        cut.FindComponents<VisorDocumento>().Should().ContainSingle().Which.Instance.DocumentoId.Should().Be(detalle.Id);
    }

    [Fact]
    public void Sin_archivo_no_hay_Descargar_PDF_ni_Ver()
    {
        var cut = RenderInformacion(Detalle(), null);

        cut.FindAll(".archivo-documento-360").Should().BeEmpty();
        cut.FindAll("[data-accion=ver-pdf]").Should().BeEmpty();
        cut.Markup.Should().Contain("Sin adjunto");
    }

    // ------------------------------------------------------------- D-23: sección «Plataformas CAE»

    [Theory]
    [InlineData(Roles.Administrador, true, true)]
    [InlineData(Roles.GestorCae, true, true)]
    [InlineData(Roles.Consulta, false, true)]
    [InlineData(Roles.Cliente, false, false)]
    public void La_seccion_Plataformas_CAE_muestra_cada_acreditacion_y_el_rol_decide_menu_y_motivo(string rol, bool veMenu, bool veMotivo)
    {
        this.ConRolDeEscritura(rol);
        var cut = RenderInformacion(Detalle(), DosAcreditaciones());

        var seccion = cut.Find("[data-seccion=plataformas-cae]");
        seccion.GetAttribute("aria-label").Should().Be("Plataformas CAE");
        seccion.QuerySelector("h3")!.TextContent.Should().Contain("Plataformas CAE").And.Contain("· 2");

        var filas = seccion.QuerySelectorAll("li");
        filas.Should().HaveCount(2, "todos los roles ven las acreditaciones que el servidor les manda");
        filas[0].QuerySelector(".plataforma-acreditacion-documento-360")!.TextContent.Trim().Should().Be("Nalanda");
        filas[0].QuerySelector(".tipo-acreditacion-documento-360")!.TextContent.Trim().Should().Be("Certificado TGSS",
            "sin nombre por plataforma (B2) se pinta el del tipo");
        filas[0].QuerySelector(".contexto-acreditacion-documento-360")!.TextContent.Trim().Should().Be("Refrigeración Alcor S.A. · Centro Norte");
        filas[0].QuerySelector(".estado-acreditacion-documento-360 .badge")!.TextContent.Trim().Should().Be("Rechazada");
        filas[1].QuerySelector(".estado-acreditacion-documento-360 .badge")!.TextContent.Trim().Should().Be("Pendiente de subir");

        var motivos = seccion.QuerySelectorAll(".motivo-acreditacion-documento-360");
        if (veMotivo)
            motivos.Should().ContainSingle().Which.TextContent.Trim().Should().Be("Motivo: Documento ilegible, el escaneo está cortado por el borde");
        else
            motivos.Should().BeEmpty("el Usuario de Cliente no ve el motivo aunque llegara");

        seccion.QuerySelectorAll("[data-menu-acreditacion]").Should().HaveCount(veMenu ? 2 : 0,
            "«Acreditación ▾» es de quien escribe; Consulta y el Usuario de Cliente leen");
    }

    [Fact]
    public void El_menu_Acreditacion_va_en_la_cabecera_de_cada_acreditacion_con_las_opciones_de_su_estado()
    {
        var cut = RenderInformacion(Detalle(), DosAcreditaciones());

        var menus = cut.FindAll(".acreditacion-documento-360 > .menu-acreditacion-documento-360 [data-menu-acreditacion]");
        menus.Select(m => m.GetAttribute("data-menu-acreditacion")).Should().Equal(
            nameof(EstadoAcreditacion.Rechazada), nameof(EstadoAcreditacion.PendienteDeSubir));
        cut.FindComponents<MenuAcreditacion>().Select(m => (m.Instance.PlataformaNombre, m.Instance.UrlPortal, m.Instance.PuedeGestionar))
            .Should().Equal(("Nalanda", "https://nalanda.example/acceso", true), ("CTAIMA", (string?)null, true));
    }

    [Theory]
    [InlineData(EstadoAcreditacion.Aceptada, EstadoVigenciaEnPlataforma.NoVenceAqui, "Validada")]
    [InlineData(EstadoAcreditacion.Aceptada, EstadoVigenciaEnPlataforma.SinConfirmar, "Sin vigencia")]
    [InlineData(EstadoAcreditacion.Rechazada, EstadoVigenciaEnPlataforma.SinConfirmar, "Rechazada")]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, EstadoVigenciaEnPlataforma.SinConfirmar, "Pendiente de subir")]
    public void El_estado_de_la_acreditacion_va_en_femenino_como_en_el_listado(EstadoAcreditacion estado, EstadoVigenciaEnPlataforma vigencia, string esperado)
    {
        var cut = RenderInformacion(Detalle(), new(EstadoDocumento.Vigente, [Acreditacion("Dokify", "Cliente", "Centro", estado, vigencia)]));

        cut.Find(".estado-acreditacion-documento-360 .badge").TextContent.Trim().Should().Be(esperado);
    }

    [Fact]
    public void Sin_acreditaciones_en_Plataformas_CAE_no_hay_seccion()
    {
        RenderInformacion(Detalle(), new(EstadoDocumento.Vigente, [])).FindAll("[data-seccion=plataformas-cae]").Should().BeEmpty();
    }

    [Fact]
    public void Un_Documento_en_el_historial_muestra_sus_acreditaciones_sin_menu_ni_dialogos_de_gestion()
    {
        var cut = RenderInformacion(Detalle() with { SustitutoId = Guid.NewGuid() }, DosAcreditaciones());

        cut.Find("[data-seccion=plataformas-cae]").QuerySelectorAll("li").Should().HaveCount(2, "el historial se lee");
        cut.FindAll("[data-menu-acreditacion]").Should().BeEmpty("el historial es inmutable, como «Renovar»");
        cut.FindAll(".menu-acreditacion-documento-360").Should().BeEmpty();
        cut.FindComponents<ModalesAcreditacion>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public void Quien_solo_lee_no_recibe_el_contenedor_del_menu(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = RenderInformacion(Detalle(), DosAcreditaciones());

        cut.FindAll(".menu-acreditacion-documento-360").Should().BeEmpty("sin menú no se reserva su columna");
    }

    [Fact]
    public void Si_la_lectura_de_acreditaciones_falla_el_panel_sigue_mostrando_el_Documento_sin_la_seccion()
    {
        var cut = RenderInformacion(Detalle() with { ArchivoUrl = "documentos/certificado.pdf" }, DosAcreditaciones(), fallaAcreditaciones: true);

        cut.Find(".acciones-documento-360").TextContent.Should().Contain("Renovar");
        cut.FindAll(".archivo-documento-360").Should().ContainSingle();
        cut.FindAll("[data-seccion=plataformas-cae]").Should().BeEmpty();
    }

    [Fact]
    public void Si_el_servidor_no_devuelve_nada_no_hay_seccion_ni_dialogos_de_acreditacion()
    {
        var cut = RenderInformacion(Detalle(), null);

        cut.FindAll("[data-seccion=plataformas-cae]").Should().BeEmpty();
        cut.FindComponents<ModalesAcreditacion>().Should().BeEmpty();
    }
}
