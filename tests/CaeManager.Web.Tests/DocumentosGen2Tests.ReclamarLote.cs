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
using System.Reflection;

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

    private Task<IRenderedComponent<PaginaDocumentos>> MarcarAsync(
        EscenarioReclamar escenario, string rol = "Administrador", params DocumentoListaDto[] marcados) =>
        MarcarEnAsync(escenario.Mediador, rol, marcados);

    private async Task<IRenderedComponent<PaginaDocumentos>> MarcarEnAsync(
        MediadorControlado mediador, string rol, DocumentoListaDto[] marcados)
    {
        var (cut, _) = Renderizar(mediador, rol: rol);
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

    // ---------------------------------------------------------------- huecos de la revisión de la PR6a

    /// <summary>Hace que la consulta de lotes se quede esperando; el caso la suelta con <see cref="Soltar"/>.</summary>
    private static TaskCompletionSource<object?> RetenerLotes(MediadorControlado mediador)
    {
        var retenida = new TaskCompletionSource<object?>();
        mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery ? retenida.Task : null;
        return retenida;
    }

    private static Task Soltar(IRenderedComponent<PaginaDocumentos> cut, TaskCompletionSource<object?> retenida,
        params LoteReclamacionAgrupadoDto[] lotes) =>
        cut.InvokeAsync(() => retenida.SetResult((IReadOnlyList<LoteReclamacionAgrupadoDto>)lotes));

    private static Task PulsarReclamarSinEsperar(IRenderedComponent<PaginaDocumentos> cut)
    {
        var boton = cut.FindComponents<Boton>().Single(b => b.Markup.Contains("Reclamar (", StringComparison.Ordinal));
        return cut.InvokeAsync(() => boton.Instance.OnClick.InvokeAsync());
    }

    [Fact]
    public async Task Un_lote_sin_ningun_Documento_seleccionado_no_llega_al_cajon()
    {
        var e = EscenarioDeReclamar();
        var ajeno = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Vencido, "Reconocimiento ajeno", "Ana Pérez");
        e.Mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery
            ? Task.FromResult<object?>((IReadOnlyList<LoteReclamacionAgrupadoDto>)
            [
                LoteDe(ClienteBetaId, "Obras Beta S.L.", Reclamable(ajeno)),
                LoteDe(ClienteAlfaId, "Constructora Alfa S.A.", Reclamable(e.Vencido)),
            ])
            : null;
        var cut = await MarcarAsync(e, marcados: [e.Vencido]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());

        cut.FindComponent<DrawerReclamacionLote>().Instance.LotesIniciales!.Select(l => l.TitularId)
            .Should().Equal([ClienteAlfaId], "el Cliente cuyo lote no trae nada de lo marcado no se ofrece");
        cut.Markup.Should().NotContain("Obras Beta S.L.");
        Toasts().Should().BeEmpty("lo marcado entró entero");
    }

    [Fact]
    public async Task En_tolerancia_cuenta_como_reclamable_y_sin_confirmar_sin_caducidad_o_faltante_no()
    {
        var enTolerancia = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.EnTolerancia, "Tipo en tolerancia", "Marta Gil Soler");
        var sinConfirmar = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.SinConfirmar, "Tipo sin confirmar", "Iván Roca Vidal");
        var sinCaducidad = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.SinCaducidad, "Tipo sin caducidad", "Nora Bosch Lara");
        var faltante = DocumentoDe(AmbitoAplicacion.Trabajador, EstadoDocumento.Faltante, "Tipo faltante", "Hugo Prat Ríos");
        var mediador = ConDocumentos(enTolerancia, sinConfirmar, sinCaducidad, faltante);

        var cut = await MarcarEnAsync(mediador, "Administrador", [enTolerancia, sinConfirmar, sinCaducidad, faltante]);

        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("4 seleccionados en esta página");
        BotonReclamar(cut).TextContent.Trim().Should().Be("Reclamar (1)", "solo el que ya pasó su vencimiento y sigue en tolerancia");
    }

    [Fact]
    public async Task Pulsar_Reclamar_con_la_consulta_en_vuelo_no_lanza_una_segunda()
    {
        var e = EscenarioDeReclamar();
        var retenida = RetenerLotes(e.Mediador);
        var cut = await MarcarAsync(e, marcados: [e.Vencido]);

        var primera = PulsarReclamarSinEsperar(cut);
        cut.WaitForAssertion(() => e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle());

        // El botón ya está en «cargando» y no deja pasar el clic; el manejador de la página lo vuelve a impedir por su cuenta.
        await PulsarReclamarSinEsperar(cut);
        e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle();

        await Soltar(cut, retenida, LoteDe(ClienteAlfaId, "Constructora Alfa S.A.", Reclamable(e.Vencido)));
        await primera;

        e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle();
        cut.FindComponents<DrawerReclamacionLote>().Should().ContainSingle();
    }

    [Fact]
    public async Task Si_la_consulta_de_lotes_falla_se_avisa_y_el_cajon_no_se_abre()
    {
        var e = EscenarioDeReclamar();
        e.Mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery
            ? Task.FromException<object?>(new InvalidOperationException("sin base de datos"))
            : null;
        var cut = await MarcarAsync(e, marcados: [e.Vencido]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());

        var aviso = Toasts().Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Error);
        aviso.Mensaje.Should().Be("No pudimos preparar la reclamación. Inténtalo de nuevo.");
        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty();
        BotonReclamar(cut).HasAttribute("disabled").Should().BeFalse("el fallo libera el botón para reintentar");
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("1 seleccionado en esta página", "la selección se conserva");
    }

    [Fact]
    public async Task Si_ningun_lote_trae_lo_marcado_solo_se_avisa_el_cajon_no_se_abre_y_la_seleccion_queda()
    {
        var e = EscenarioDeReclamar();
        e.Mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery
            ? Task.FromResult<object?>((IReadOnlyList<LoteReclamacionAgrupadoDto>)[])
            : null;
        var cut = await MarcarAsync(e, marcados: [e.Vencido, e.PorVencer]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());

        e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle("la consulta sí se hizo");
        var aviso = Toasts().Should().ContainSingle().Subject;
        aviso.Tono.Should().Be(TonoToast.Advertencia);
        aviso.Mensaje.Should().StartWith("2 de 2 documentos no entran");
        // Sin lotes, un cajón abierto diría «Nada que reclamar a esta Empresa»: aquí no se abre.
        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty();
        cut.Markup.Should().NotContain("Nada que reclamar");
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Should().Be("2 seleccionados en esta página", "la selección no se toca");
        BotonReclamar(cut).HasAttribute("disabled").Should().BeFalse("la preparación terminó y el botón queda libre");
    }

    [Fact]
    public async Task El_aviso_de_lo_no_cubierto_cita_la_ventana_de_reclamacion_y_la_Cartera_sin_vocabulario_prohibido()
    {
        var e = EscenarioDeReclamar();
        e.Mediador.Interceptar = p => p is ObtenerLoteReclamacionPorFiltroQuery
            ? Task.FromResult<object?>((IReadOnlyList<LoteReclamacionAgrupadoDto>)[])
            : null;
        var cut = await MarcarAsync(e, marcados: [e.Vencido]);

        await BotonReclamar(cut).ClickAsync(new MouseEventArgs());

        var mensaje = Toasts().Should().ContainSingle().Subject.Mensaje;
        mensaje.Should().Contain("ventana de reclamación").And.Contain("ya renovados").And.Contain("Asignación activa")
            .And.Contain("o que el Centro o su Cliente queden fuera de tu Cartera");
        mensaje.Should().NotContain("alcance").And.NotContain("Cliente empresarial");
    }

    [Fact]
    public async Task El_aviso_de_lo_no_cubierto_dice_lo_mismo_en_ca_ES_que_en_es()
    {
        var raiz = new DirectoryInfo(AppContext.BaseDirectory);
        while (raiz is not null && !File.Exists(Path.Combine(raiz.FullName, "CaeManager.slnx"))) raiz = raiz.Parent;
        var recursos = Path.Combine(raiz!.FullName, "src", "CaeManager.Web", "Features", "Documentos", "Recursos");

        async Task<string> ValorAsync(string fichero)
        {
            var xml = System.Xml.Linq.XDocument.Parse(await File.ReadAllTextAsync(Path.Combine(recursos, fichero)));
            return xml.Descendants("data").Single(d => (string?)d.Attribute("name") == "ToastReclamarLoteFuera").Element("value")!.Value;
        }

        var es = await ValorAsync("TextosDocumentos.resx");
        var ca = await ValorAsync("TextosDocumentos.ca-ES.resx");

        es.Should().Contain("{0} de {1} documentos no entran en ninguna reclamación: puede que venzan fuera de la ventana de reclamación")
            .And.Contain("que el Centro o su Cliente queden fuera de tu Cartera");
        ca.Should().Contain("{0} de {1} documents no entren en cap reclamació: pot ser que venguin fora de la finestra de reclamació")
            .And.Contain("que el Centre o el seu Client quedin fora de la teva Cartera");
        foreach (var texto in new[] { es, ca })
            texto.Should().NotContain("alcance").And.NotContain("Cliente empresarial").And.NotContain("Client empresarial");
    }

    [Fact]
    public async Task Si_cambia_el_contexto_mientras_se_preparan_los_lotes_el_cajon_no_se_abre()
    {
        var e = EscenarioDeReclamar();
        var retenida = RetenerLotes(e.Mediador);
        var cut = await MarcarAsync(e, marcados: [e.Vencido]);

        var pendiente = PulsarReclamarSinEsperar(cut);
        cut.WaitForAssertion(() => e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle());

        await AlternarFranjaDocumentoFase1(cut, "Vencidos");
        await Soltar(cut, retenida, LoteDe(ClienteAlfaId, "Constructora Alfa S.A.", Reclamable(e.Vencido)));
        await pendiente;

        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty("los lotes eran de la selección de otro contexto");
        Toasts().Should().BeEmpty();
    }

    /// <summary>
    /// Cambiar de página, de orden o de tamaño de página no cambia el contexto documental, pero recarga la lista y suelta la
    /// selección. Se prueba con el orden, que es el cambio que el arnés sabe provocar sin tocar los filtros.
    /// </summary>
    [Fact]
    public async Task Si_la_lista_se_recarga_mientras_se_preparan_los_lotes_el_cajon_no_se_abre()
    {
        var e = EscenarioDeReclamar();
        var retenida = RetenerLotes(e.Mediador);
        var cut = await MarcarAsync(e, marcados: [e.Vencido]);

        var pendiente = PulsarReclamarSinEsperar(cut);
        cut.WaitForAssertion(() => e.Mediador.Enviadas.OfType<ObtenerLoteReclamacionPorFiltroQuery>().Should().ContainSingle());

        await CabeceraDocumentoFase1(cut, "Vence").QuerySelector("button.col-title")!.ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".barra-acciones-lote").Should().BeEmpty("la recarga soltó la selección"));

        await Soltar(cut, retenida, LoteDe(ClienteAlfaId, "Constructora Alfa S.A.", Reclamable(e.Vencido)));
        await pendiente;

        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty("la selección sobre la que se calcularon los lotes ya no existe");
        Toasts().Should().BeEmpty();
    }

    /// <summary>
    /// Consulta ya no llega a seleccionar nada, así que el caso de arriba es vacuo: aquí se fuerza una selección por reflexión
    /// (el estado interno de la página) para comprobar que la barra y «Reclamar» dependen también de la escritura, no solo de la selección.
    /// </summary>
    [Fact]
    public async Task Con_una_seleccion_forzada_sin_escritura_la_barra_y_Reclamar_siguen_ocultos()
    {
        var e = EscenarioDeReclamar();
        var (cut, _) = Renderizar(e.Mediador, rol: "Consulta");
        cut.WaitForAssertion(() => cut.FindAll("tbody tr").Should().NotBeEmpty());

        var seleccionados = (HashSet<Guid>)typeof(PaginaDocumentos)
            .GetField("_seleccionados", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(cut.Instance)!;
        await cut.InvokeAsync(() => seleccionados.Add(e.Vencido.Id));
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.OnAtajo.InvokeAsync("j"));

        seleccionados.Should().Contain(e.Vencido.Id, "control positivo: la selección forzada sigue ahí tras volver a pintar");
        cut.Find("tbody tr").ClassList.Should().Contain("fila-enfocada", "control positivo: la página volvió a pintarse");
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        cut.Markup.Should().NotContain("Reclamar (");
        cut.FindComponents<DrawerReclamacionLote>().Should().BeEmpty();
    }
}
