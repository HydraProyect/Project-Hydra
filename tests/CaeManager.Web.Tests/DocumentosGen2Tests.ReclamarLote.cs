using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Contactos;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresa;
using CaeManager.Application.Reclamaciones.Commands.EnviarReclamacionEmpresaEmpleadora;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

using PaginaDocumentos = CaeManager.Web.Features.Documentos.Pages.Documentos;

/// <summary>
/// PR6a del lienzo «Documentos · listado sin pestañas»: «Reclamar (n)» en la barra de selección en lote. Cuenta solo los Documentos
/// de Trabajador vencidos o por vencer de la selección, abre el cajón de reclamación de siempre con los lotes de esos Documentos
/// (agrupados por Cliente empresarial, como en Alertas) y no envía nada por sí mismo. También fija lo que la barra ya tenía en main
/// («Eliminar seleccionados», «Cancelar» y el recuento).
/// <para>
/// No observa la autorización ni el alcance de cartera del Command de envío ni el correo (los decide Application; el cajón se prueba
/// en <c>DrawerReclamacionLote*Tests</c>); aquí la consulta de lotes es un doble y solo se mira lo que la página hace con lo que
/// recibe.
/// </para>
/// </summary>
public partial class DocumentosGen2Tests
{
    private static readonly Guid ClienteAlfaId = Guid.NewGuid();
    private static readonly Guid ClienteBetaId = Guid.NewGuid();

    private static DocumentoListaDto DocumentoDe(AmbitoAplicacion ambito, EstadoDocumento estado, string tipo, string propietario) => new(
        Guid.NewGuid(), ambito, propietario, tipo, new DateOnly(2026, 1, 15), new DateOnly(2026, 11, 20), estado,
        ArchivoUrl: null, Acreditaciones: []);

    private static DocumentoReclamableDto Reclamable(DocumentoListaDto documento) => new(
        documento.Id, Guid.NewGuid(), documento.PropietarioNombre, Guid.NewGuid(), documento.TipoDocumentoNombre,
        new DateOnly(2026, 11, 20), documento.Estado);

    private static LoteReclamacionAgrupadoDto LoteDe(Guid titularId, string titular, params DocumentoReclamableDto[] documentos) => new(
        titularId, titular, AmbitoAplicacion.Trabajador, null, documentos, null,
        [new DestinatarioAgendaDto(Guid.NewGuid(), "Contacto " + titular, "prl@ejemplo.invalid", ["Contacto predeterminado"])]);

    /// <summary>Escenario del artboard: de cinco Documentos, solo tres son de Trabajador vencidos o por vencer.</summary>
    private sealed record EscenarioReclamar(
        DocumentoListaDto Vencido, DocumentoListaDto PorVencer, DocumentoListaDto Proximo, DocumentoListaDto DeEmpresa,
        DocumentoListaDto Vigente, MediadorControlado Mediador);

    private static EscenarioReclamar EscenarioDeReclamar()
    {
        var vencido = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Vencido, "Aptitud médica", "Lucía Ferrer Mora");
        var porVencer = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Urgente, "Formación PRL 20 h", "Raúl Mena Costa");
        var proximo = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Proximo, "Carnet de carretillero", "Carmen Duarte Vela");
        var deEmpresa = DocumentoDe(AmbitoAplicacion.Empresa, EstadoDocumento.Urgente, "Certificado TGSS", "Instalaciones Valmar S.L.");
        var vigente = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Vigente, "DNI", "Pablo Ríos Gil");

        var mediador = ConDocumentos(vencido, porVencer, proximo, deEmpresa, vigente);
        // El Cliente Alfa trae dos de los seleccionados y uno que no se marcó; el Cliente Beta, el tercero.
        var noMarcado = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Vencido, "Reconocimiento no marcado", "Ana Pérez");
        mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery
            ? Task.FromResult<object?>((IReadOnlyList<LoteReclamacionAgrupadoDto>)
            [
                LoteDe(ClienteAlfaId, "Constructora Alfa S.A.", Reclamable(vencido), Reclamable(porVencer), Reclamable(noMarcado)),
                LoteDe(ClienteBetaId, "Obras Beta S.L.", Reclamable(proximo)),
            ])
            : null;
        return new(vencido, porVencer, proximo, deEmpresa, vigente, mediador);
    }

    private static IElement CasillaDeDocumento(IRenderedComponent<PaginaDocumentos> cut, DocumentoListaDto documento) =>
        cut.FindAll(".tabla-datos tbody input[type=checkbox]")
            .Single(c => c.GetAttribute("aria-label")!.StartsWith($"Seleccionar {documento.TipoDocumentoNombre} de ", StringComparison.Ordinal));

    private async Task<IRenderedComponent<PaginaDocumentos>> MarcarAsync(
        EscenarioReclamar escenario, string rol = "Administrador", params DocumentoListaDto[] marcados)
    {
        var (cut, _) = Renderizar(escenario.Mediador, rol: rol);
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label='Selección múltiple']").Should().NotBeEmpty());
        await BotonPorTexto(cut, "button[aria-label]", "Selección múltiple").ClickAsync(new MouseEventArgs());
        foreach (var documento in marcados)
            await CasillaDeDocumento(cut, documento).ChangeAsync(new ChangeEventArgs { Value = true });
        return cut;
    }

    private static IElement BotonReclamar(IRenderedComponent<PaginaDocumentos> cut) =>
        cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim().StartsWith("Reclamar (", StringComparison.Ordinal));

    [Fact]
    public async Task Reclamar_cuenta_solo_los_Documentos_de_Trabajador_vencidos_o_por_vencer_de_la_seleccion()
    {
        var e = EscenarioDeReclamar();
        var cut = await MarcarAsync(e, marcados: [e.Vencido, e.PorVencer, e.Proximo, e.DeEmpresa, e.Vigente]);

        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("5 seleccionados en esta página");
        var boton = BotonReclamar(cut);
        boton.TextContent.Trim().Should().Be("Reclamar (3)", "vencido, urgente y próximo; el de Empresa y el vigente no entran");
        boton.HasAttribute("disabled").Should().BeFalse();
        boton.ClassList.Should().Contain("boton-secundario").And.NotContain("boton-primario", "la barra no tiene acción primaria: solo la de la cabecera");
    }

    [Fact]
    public async Task Reclamar_con_cero_reclamables_sale_deshabilitado_con_su_ayuda_y_no_abre_nada()
    {
        var e = EscenarioDeReclamar();
        var cut = await MarcarAsync(e, marcados: [e.DeEmpresa, e.Vigente]);

        var boton = BotonReclamar(cut);
        boton.TextContent.Trim().Should().Be("Reclamar (0)");
        boton.HasAttribute("disabled").Should().BeTrue();
        boton.GetAttribute("title").Should().Contain("vencidos o por vencer");

        await boton.ClickAsync(new MouseEventArgs());

        e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().BeEmpty();
        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty();
    }

    [Fact]
    public async Task Reclamar_abre_el_cajon_con_los_lotes_de_lo_marcado_agrupados_por_Cliente_empresarial()
    {
        var e = EscenarioDeReclamar();
        var cut = await MarcarAsync(e, marcados: [e.Vencido, e.PorVencer, e.Proximo, e.DeEmpresa]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());

        var consulta = e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle().Subject;
        consulta.Filtro.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
        consulta.PorEmpresaEmpleadora.Should().BeTrue("como Alertas: el Trabajador de Empresa se reclama a su Empresa empleadora");

        var cajon = cut.FindComponent<DrawerReclamacionLote>().Instance;
        cajon.Visible.Should().BeTrue();
        cajon.LotesIniciales.Should().NotBeNull();
        cajon.LotesIniciales!.Select(l => l.TitularNombre).Should().Equal("Constructora Alfa S.A.", "Obras Beta S.L.");
        cajon.LotesIniciales.Single(l => l.TitularId == ClienteAlfaId).Documentos.Select(d => d.DocumentoId)
            .Should().BeEquivalentTo([e.Vencido.Id, e.PorVencer.Id], "el documento del Cliente que no se marcó no entra");
        cajon.LotesIniciales.Single(l => l.TitularId == ClienteBetaId).Documentos.Select(d => d.DocumentoId)
            .Should().BeEquivalentTo([e.Proximo.Id]);
        cajon.LotesIniciales.SelectMany(l => l.Documentos).Select(d => d.DocumentoId)
            .Should().NotContain(e.DeEmpresa.Id, "el de Empresa no se reclama desde aquí");

        cut.Markup.Should().Contain("Constructora Alfa S.A.").And.Contain("Obras Beta S.L.");
        cut.Markup.Should().NotContain("Reconocimiento no marcado");
        Toasts().Should().BeEmpty("todo lo reclamable estaba en la ventana de reclamación");

        e.Mediador.Enviadas.Should().NotContain(c => c is EnviarReclamacionCommand || c is EnviarReclamacionEmpresaCommand || c is EnviarReclamacionEmpresaEmpleadoraCommand,
            "abrir el cajón no envía nada: lo envía el Gestor CAE desde el cajón");
    }

    [Fact]
    public async Task Reclamar_avisa_de_lo_reclamable_que_ningun_lote_trae()
    {
        var e = EscenarioDeReclamar();
        e.Mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery
            ? Task.FromResult<object?>((IReadOnlyList<LoteReclamacionAgrupadoDto>)
                [LoteDe(ClienteAlfaId, "Constructora Alfa S.A.", Reclamable(e.Vencido))])
            : null;
        var cut = await MarcarAsync(e, marcados: [e.Vencido, e.PorVencer]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());

        Toasts().Select(t => t.Mensaje).Should().ContainSingle().Which.Should().StartWith("1 de 2 documentos no entran");
        cut.FindComponent<DrawerReclamacionLote>().Instance.LotesIniciales!.Should().ContainSingle();
    }

    [Fact]
    public async Task Cerrar_el_cajon_tras_un_envio_suelta_la_seleccion_y_sin_envio_la_conserva()
    {
        var e = EscenarioDeReclamar();
        var cut = await MarcarAsync(e, marcados: [e.Vencido, e.PorVencer]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());
        var cajon = cut.FindComponent<DrawerReclamacionLote>().Instance;
        await cut.InvokeAsync(() => cajon.VisibleChanged.InvokeAsync(false));

        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty();
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("2 seleccionados en esta página",
            "cerrar sin enviar no suelta la selección");

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());
        cajon = cut.FindComponent<DrawerReclamacionLote>().Instance;
        await cut.InvokeAsync(() => cajon.OnReclamacionEnviada.InvokeAsync());
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("2 seleccionados en esta página",
            "el cajón avisa tras cada lote y puede quedar otro por revisar: la selección sigue hasta cerrarlo");
        await cut.InvokeAsync(() => cajon.VisibleChanged.InvokeAsync(false));

        cut.FindAll(".barra-acciones-lote").Should().BeEmpty("lo enviado cierra la selección");
        cut.FindAll(".tabla-datos tbody input[type=checkbox]:checked").Should().BeEmpty();
    }

    [Fact]
    public async Task Consulta_no_ve_Reclamar_ni_la_barra_de_lote()
    {
        var e = EscenarioDeReclamar();
        var (cut, _) = Renderizar(e.Mediador, rol: "Consulta");
        cut.WaitForAssertion(() => e.Mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().NotBeEmpty());

        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("x"));

        cut.Find("tbody tr").ClassList.Should().Contain("fila-enfocada", "control positivo: los atajos llegaron a la lista");
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        cut.Markup.Should().NotContain("Reclamar (");
        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty();
    }

    [Fact]
    public async Task La_barra_de_lote_conserva_Eliminar_seleccionados_Cancelar_y_el_recuento()
    {
        var e = EscenarioDeReclamar();
        var cut = await MarcarAsync(e, marcados: [e.Vencido, e.Vigente]);

        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("2 seleccionados en esta página");
        var textos = cut.FindAll(".barra-acciones-lote button").Select(b => b.TextContent.Trim()).ToList();
        textos.Should().Equal("Reclamar (1)", "Cancelar", "Eliminar seleccionados");
        cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados")
            .ClassList.Should().Contain("boton-destructivo");

        await BotonPorTexto(cut, ".barra-acciones-lote button", "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        cut.Markup.Should().Contain("Se ocultarán de las listas activas", "Eliminar sigue pidiendo confirmación");

        await BotonPorTexto(cut, ".barra-acciones-lote button", "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty("«Cancelar» suelta la selección");
    }
}
