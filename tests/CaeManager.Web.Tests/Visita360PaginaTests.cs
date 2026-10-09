using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Visitas.Commands.EnviarPaqueteAcreditacionVisita;
using CaeManager.Application.Visitas.Commands.MarcarDocumentacionGestionada;
using Microsoft.AspNetCore.Components.Web;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Application.Visitas.Queries.ObtenerAvisoVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Visitas.Components;
using CaeManager.Web.Features.Visitas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Visita 360, la página (<see cref="VisitaDetalle"/>, <c>/visitas/{id}</c>), primer
/// incremento. Prueban efectos: qué se pinta en cada rama, qué consultas y comandos salen y
/// qué conserva el rol Consulta. bUnit no evalúa CSS: ninguno afirma un estilo.
///
/// <para>
/// Que Consulta no vea «Enviar por correo» ni «Descargar ZIP» es aquí solo lo que se pinta.
/// Quien se lo niega es <c>ObtenerPaqueteDocumentalVisitaQuery</c>, y eso lo prueba
/// <c>PaqueteDocumentalVisitaCorreoTests</c> contra el handler real.
/// </para>
/// </summary>
public class Visita360PaginaTests : BunitContext
{
    public Visita360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Tienen sus propios tests y servicios; aquí solo importa cuándo se montan.
        ComponentFactories.AddStub<VisorDocumento>();
        ComponentFactories.AddStub<RedactarMensajeDrawer>();
        ComponentFactories.AddStub<EditarVisitaDrawer>();
    }

    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();
    private static readonly Guid VisitaId = Guid.NewGuid();
    private static readonly Guid Paula = Guid.NewGuid();
    private static readonly Guid Diego = Guid.NewGuid();

    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, DetalleVisitaDto> Detalles { get; } = [];
        public Dictionary<Guid, TaskCompletionSource<DetalleVisitaDto?>> DetallesDiferidos { get; } = [];
        public DocumentacionVisitaDto Documentacion { get; set; } = new(Guid.NewGuid(), new(EstadoDocumento.Vigente, []), []);
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerDetalleVisitaQuery diferida && DetallesDiferidos.TryGetValue(diferida.Id, out var pendiente))
                return (Task<TResponse>)(object)pendiente.Task;

            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request)
        {
            switch (request)
            {
                case ObtenerDetalleVisitaQuery q:
                    return Detalles.GetValueOrDefault(q.Id);
                case ObtenerDocumentacionVisitaQuery:
                    return Documentacion;
                case ObtenerSolicitudAccesoCorreoQuery:
                    return Result.Exito(new SolicitudAccesoCorreoDto("accesos@centro.example", "Solicitud de acceso — Sede Sevilla", "Buenos días"));
                case ObtenerAvisoVisitaQuery:
                    return Result.Exito(new AvisoVisitaDto("Aviso de visita — Sede Sevilla", "Mañana acuden dos técnicos."));
                case ObtenerPaqueteDocumentalVisitaQuery:
                    return Result.Exito(new PaqueteDocumentalDescargaDto("paquete.zip", [1, 2, 3]));
                case MarcarDocumentacionGestionadaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { DocumentacionGestionadaEnUtc = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), Version = Guid.NewGuid() };
                    return Result.Exito();
                case EnviarPaqueteAcreditacionVisitaCommand c:
                    Detalles[c.VisitaId] = Detalles[c.VisitaId] with { DocumentacionGestionadaEnUtc = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), Version = Guid.NewGuid() };
                    return Result.Exito(Guid.NewGuid());
                case MarcarNotificadoClienteCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { NotificadoCliente = c.Notificado };
                    return Result.Exito();
                case CancelarVisitaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { EstaCancelada = true, MotivoCancelacion = c.Motivo };
                    return Result.Exito(new VisitaCanceladaDto(c.Id, Guid.NewGuid()));
                case ReactivarVisitaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { EstaCancelada = false, MotivoCancelacion = null };
                    return Result.Exito();
                default:
                    throw new NotSupportedException(request.GetType().Name);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    // ── Montaje ───────────────────────────────────────────────────────────

    private static DetalleVisitaDto Detalle(
        Guid? id = null, string centro = "Sede Sevilla", bool requiereGestion = true, bool porCorreo = true, bool cancelada = false) => new(
        id ?? VisitaId, centro, "Titular Demo S.A.", Guid.NewGuid(), "Contratista Demo S.L.", Hoy, Hoy,
        Notas: "Puerta de servicio.", NotificadoCliente: false,
        Trabajadores: [new(Paula, "Paula Campos"), new(Diego, "Diego Ruiz")],
        HoraEstimadaAcceso: new TimeOnly(8, 0), FechaHoraSolicitudUtc: null, FechaHoraExpedienteCompletoUtc: null,
        AntelacionNominalHoras: null, AntelacionEfectivaHoras: null, Tramo: null, AtribucionUrgencia.SinUrgencia,
        CentroRequiereGestionCae: requiereGestion, CentroGestionadoPorCorreo: porCorreo,
        EstaCancelada: cancelada, MotivoCancelacion: cancelada ? "Aplazada" : null, Version: Guid.NewGuid(),
        CentroId: Guid.NewGuid(), EmpresaTitularId: Guid.NewGuid(), Origen: OrigenVisita.Correo, NivelUrgencia: NivelUrgenciaVisita.Normal);

    private static DocumentoVisitaItemDto Documento(string tipo, EstadoDocumento estado, Guid? trabajadorId = null) => new(
        estado == EstadoDocumento.Faltante ? null : Guid.NewGuid(), trabajadorId, Guid.NewGuid(), tipo, estado,
        estado is EstadoDocumento.Faltante or EstadoDocumento.SinConfirmar ? null : Hoy.AddDays(estado == EstadoDocumento.Vencido ? -3 : 200),
        estado == EstadoDocumento.Faltante ? null : "blob/clave.pdf");

    /// <summary>Paula con un vencido y un pendiente; Diego al día; la empresa al día.</summary>
    private static DocumentacionVisitaDto DocumentacionConIncidencias() => new(
        Guid.NewGuid(),
        new(EstadoDocumento.Vigente, [Documento("RNT", EstadoDocumento.Vigente)]),
        [
            new(Paula, "Paula Campos", "Contratista Demo S.L.", new(EstadoDocumento.Faltante,
            [
                Documento("Aptitud médica", EstadoDocumento.Vencido, Paula),
                Documento("Entrega de EPI", EstadoDocumento.Faltante, Paula),
            ])),
            new(Diego, "Diego Ruiz", "Contratista Demo S.L.", new(EstadoDocumento.Proximo,
            [
                Documento("Aptitud médica", EstadoDocumento.Proximo, Diego),
                Documento("Formación PRL", EstadoDocumento.Vigente, Diego),
            ])),
        ]);

    private (IRenderedComponent<VisitaDetalle> Cut, MediatorFalso Mediador) Montar(
        DetalleVisitaDto? detalle, string rol = Roles.GestorCae, DocumentacionVisitaDto? documentacion = null)
    {
        var mediador = new MediatorFalso();
        if (detalle is not null)
            mediador.Detalles[detalle.Id] = detalle;
        if (documentacion is not null)
            mediador.Documentacion = documentacion;

        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();

        var cut = Render<VisitaDetalle>(p => p.Add(x => x.VisitaId, detalle?.Id ?? VisitaId));
        return (cut, mediador);
    }

    private static IEnumerable<string> Botones(IRenderedComponent<VisitaDetalle> cut) =>
        cut.FindAll("button, a").Select(b => b.TextContent.Trim());

    // ── Ramas ─────────────────────────────────────────────────────────────

    [Fact]
    public void Centro_gestionado_por_correo_pinta_comprobacion_y_paquete_con_enviar_como_accion_principal()
    {
        var (cut, mediador) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.FindAll("[role=tab]").Select(t => t.TextContent).Should().SatisfyRespectively(
            t => t.Should().Contain("Comprobación previa"),
            t => t.Should().Contain("Paquete de acreditación"));
        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().Contain("Enviar por correo");
        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().Be("1 de 2 trabajadores listos para entrar");

        mediador.Enviadas.OfType<ObtenerDocumentacionVisitaQuery>().Should().ContainSingle();
        mediador.Enviadas.OfType<ObtenerSolicitudAccesoCorreoQuery>().Should().ContainSingle();
        mediador.Enviadas.OfType<ObtenerAvisoVisitaQuery>().Should().BeEmpty();
        // Abrir la página no construye el paquete: eso registra accesos a documentos sensibles.
        mediador.Enviadas.OfType<ObtenerPaqueteDocumentalVisitaQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Centro_con_gestion_y_sin_correo_se_ve_como_hoy_solo_la_comprobacion_previa()
    {
        var (cut, mediador) = Montar(Detalle(porCorreo: false), documentacion: DocumentacionConIncidencias());

        cut.FindAll("[role=tab]").Should().ContainSingle().Which.TextContent.Should().Contain("Comprobación previa");
        Botones(cut).Should().NotContain(["Enviar por correo", "Copiar solicitud", "Descargar ZIP de documentación"]);
        mediador.Enviadas.OfType<ObtenerSolicitudAccesoCorreoQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Centro_sin_gestion_CAE_pinta_el_aviso_y_no_comprueba_documentacion()
    {
        var (cut, mediador) = Montar(Detalle(requiereGestion: false, porCorreo: false));

        cut.FindAll("[role=tab]").Should().ContainSingle().Which.TextContent.Should().Contain("Aviso de visita");
        cut.Markup.Should().Contain("Mañana acuden dos técnicos.");
        cut.FindAll("[data-pieza=anillo]").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentacionVisitaQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerSolicitudAccesoCorreoQuery>().Should().BeEmpty();
    }

    [Fact]
    public void Visita_cancelada_solo_ofrece_reactivar_y_no_carga_nada_mas()
    {
        var (cut, mediador) = Montar(Detalle(cancelada: true));

        cut.Markup.Should().Contain("Visita cancelada. Motivo: Aplazada");
        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().Contain("Reactivar");
        cut.FindAll(".menu-acciones-boton").Should().BeEmpty();
        cut.Find("input[role=switch]").HasAttribute("disabled").Should().BeTrue();
        mediador.Enviadas.Should().ContainSingle().Which.Should().BeOfType<ObtenerDetalleVisitaQuery>();
    }

    [Fact]
    public void Visita_no_encontrada_o_fuera_de_alcance_se_ve_como_un_fallo_de_carga()
    {
        var (cut, _) = Montar(detalle: null);

        cut.Markup.Should().Contain("No pudimos cargar esta visita");
        cut.FindAll("[data-pieza=cabecera-identidad]").Should().BeEmpty();
    }

    // ── Rol Consulta ──────────────────────────────────────────────────────

    [Fact]
    public void Consulta_conserva_copiar_la_solicitud_y_pierde_enviar_descargar_menu_e_interruptor()
    {
        var (cut, _) = Montar(Detalle(), Roles.Consulta, DocumentacionConIncidencias());

        Botones(cut).Should().Contain("Copiar solicitud");
        Botones(cut).Should().NotContain(["Enviar por correo", "Descargar ZIP de documentación", "Editar →"]);
        cut.FindAll(".menu-acciones-boton").Should().BeEmpty();
        cut.Find("input[role=switch]").HasAttribute("disabled").Should().BeTrue();
        // La banda nombra las incidencias, pero sin escritura no son botones.
        cut.Find("[data-pieza=banda]").TextContent.Should().Contain("Aptitud médica · Vencido");
        cut.FindAll("[data-pieza=banda] button").Should().BeEmpty();

        cut.FindAll("[role=tab]")[1].Click();
        Botones(cut).Should().Contain("Copiar solicitud");
        Botones(cut).Should().NotContain(["Enviar por correo", "Descargar ZIP de documentación"]);
    }

    [Fact]
    public void Con_escritura_la_pestana_del_paquete_ofrece_enviar_copiar_y_descargar()
    {
        var (cut, _) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.FindAll("[role=tab]")[1].Click();

        var panel = cut.FindAll(".visita360-botones").Should().ContainSingle().Subject;
        panel.TextContent.Should().Contain("Enviar por correo").And.Contain("Copiar solicitud").And.Contain("Descargar ZIP de documentación");
        panel.QuerySelector("a[download]")!.GetAttribute("href").Should().Be($"/visitas/{VisitaId}/paquete-documental.zip");
    }

    [Fact]
    public void Consulta_no_puede_reactivar_una_cancelada()
    {
        var (cut, _) = Montar(Detalle(cancelada: true), Roles.Consulta);

        Botones(cut).Should().NotContain("Reactivar");
    }

    // ── Regla «listo» ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(EstadoDocumento.Vigente, true)]
    [InlineData(EstadoDocumento.SinCaducidad, true)]
    [InlineData(EstadoDocumento.Proximo, true)]
    [InlineData(EstadoDocumento.Urgente, true)]
    [InlineData(EstadoDocumento.EnTolerancia, true)]
    [InlineData(EstadoDocumento.SinConfirmar, false)]
    [InlineData(EstadoDocumento.Vencido, false)]
    [InlineData(EstadoDocumento.Faltante, false)]
    public void Un_trabajador_esta_listo_si_todo_lo_exigido_esta_al_dia(EstadoDocumento estado, bool listo)
    {
        var seccion = new SeccionDocumentacionDto(estado,
            [Documento("Formación PRL", EstadoDocumento.Vigente, Paula), Documento("Aptitud médica", estado, Paula)]);

        VisitaDetalle.EstaListo(seccion).Should().Be(listo);
    }

    [Fact]
    public void Sin_trabajadores_no_hay_anillo_ni_banda_y_se_ofrece_editar()
    {
        var (cut, _) = Montar(Detalle(), documentacion: new(Guid.NewGuid(), new(EstadoDocumento.Vigente, []), []));

        cut.FindAll("[data-pieza=anillo]").Should().BeEmpty();
        cut.FindAll("[data-pieza=banda]").Should().BeEmpty();
        cut.Markup.Should().Contain("Sin trabajadores");
    }

    // ── Comprobación previa ───────────────────────────────────────────────

    [Fact]
    public void Los_grupos_van_del_peor_estado_al_mejor_y_la_fila_con_problema_lleva_tono()
    {
        var (cut, _) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.FindAll(".visita360-grupo span").Select(s => s.TextContent)
            .Should().Equal("Paula Campos", "Diego Ruiz", "Documentación de la empresa");
        cut.FindAll("[data-pieza=fila][data-tono=peligro]").Should().HaveCount(2);
        cut.FindAll("[data-pieza=fila]").Should().HaveCount(5);
        // Vocabulario de las páginas 360: «Pendiente», no «Falta»; Próximo se lee «Por vencer».
        cut.FindAll("[data-pieza=fila] [data-pieza=pastilla]").Select(p => p.TextContent.Trim())
            .Should().Equal("Vencido", "Pendiente", "Por vencer", "Vigente", "Vigente");
    }

    [Fact]
    public void El_filtro_de_estados_es_multiple_y_Todos_lo_borra()
    {
        var (cut, _) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());
        IElementoFiltro Filtro(string texto) => new(cut.FindAll(".filtro-estados-opcion").Single(b => b.TextContent.StartsWith(texto, StringComparison.Ordinal)));

        Filtro("Vencido").Clic();
        cut.FindAll("[data-pieza=fila]").Should().ContainSingle();
        // El grupo que se queda sin filas no se pinta.
        cut.FindAll(".visita360-grupo span").Select(s => s.TextContent).Should().Equal("Paula Campos");

        Filtro("Pendiente").Clic();
        cut.FindAll("[data-pieza=fila]").Should().HaveCount(2);

        Filtro("Todos").Clic();
        cut.FindAll("[data-pieza=fila]").Should().HaveCount(5);
    }

    private sealed record IElementoFiltro(AngleSharp.Dom.IElement Elemento)
    {
        public void Clic() => Elemento.Click();
    }

    [Fact]
    public void Plegar_un_grupo_oculta_sus_filas()
    {
        var (cut, _) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.Find("button[aria-label='Plegar Paula Campos']").Click();

        cut.FindAll("[data-pieza=fila]").Should().HaveCount(3);
        cut.Find("button[aria-label='Desplegar Paula Campos']").GetAttribute("aria-expanded").Should().Be("false");
    }

    // ── Acciones ──────────────────────────────────────────────────────────

    [Fact]
    public void El_interruptor_envia_el_mismo_comando_que_el_del_listado()
    {
        var (cut, mediador) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.Find("input[role=switch]").Change(true);

        mediador.Enviadas.OfType<MarcarNotificadoClienteCommand>().Should().ContainSingle()
            .Which.Should().Be(new MarcarNotificadoClienteCommand(VisitaId, true));
        cut.Find("input[role=switch]").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public void Enviar_por_correo_construye_el_paquete_con_la_consulta_de_la_descarga_y_abre_el_compositor()
    {
        var (cut, mediador) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.FindAll("[data-pieza=cabecera-identidad] button").Single(b => b.TextContent.Contains("Enviar por correo")).Click();

        mediador.Enviadas.OfType<ObtenerPaqueteDocumentalVisitaQuery>().Should().ContainSingle()
            .Which.VisitaId.Should().Be(VisitaId);
        var compositor = cut.FindComponent<Stub<RedactarMensajeDrawer>>().Instance.Parameters;
        compositor.Get(x => x.Visible).Should().BeTrue();
        compositor.Get(x => x.DestinatariosIniciales).Should().Be("accesos@centro.example");
        compositor.Get(x => x.Adjunto)!.NombreArchivo.Should().Be("paquete.zip");
    }

    // ── Documentación gestionada (estado guardado, decisión del 2026-10-09) ──

    private static string Cabecera(IRenderedComponent<VisitaDetalle> cut) =>
        cut.Find("[data-pieza=cabecera-identidad]").TextContent;

    [Fact]
    public void La_cabecera_dice_por_gestionar_hasta_que_la_visita_tiene_la_marca()
    {
        var (cut, _) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        Cabecera(cut).Should().Contain("Documentación por gestionar").And.NotContain("Documentación gestionada");
    }

    [Fact]
    public void Con_la_marca_la_cabecera_dice_gestionada_y_el_menu_ya_no_ofrece_marcarla()
    {
        var gestionada = Detalle() with { DocumentacionGestionadaEnUtc = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc) };
        var (cut, _) = Montar(gestionada, documentacion: DocumentacionConIncidencias());

        Cabecera(cut).Should().Contain("Documentación gestionada").And.NotContain("por gestionar");
        cut.Markup.Should().NotContain("Marcar documentación gestionada");
        cut.Markup.Should().Contain("Gestionada el");
    }

    [Fact]
    public void En_un_Centro_sin_gestion_CAE_la_cabecera_no_habla_de_documentacion_por_gestionar()
    {
        var (cut, _) = Montar(Detalle(requiereGestion: false, porCorreo: false));

        Cabecera(cut).Should().NotContain("por gestionar").And.NotContain("Documentación gestionada");
        cut.Markup.Should().NotContain("Marcar documentación gestionada");
    }

    [Fact]
    public async Task Marcar_desde_el_menu_manda_el_comando_con_la_version_y_la_cabecera_pasa_a_gestionada()
    {
        var detalle = Detalle();
        var (cut, mediador) = Montar(detalle, documentacion: DocumentacionConIncidencias());

        await cut.Find("[data-pieza=cabecera-identidad] .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Marcar documentación gestionada").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<MarcarDocumentacionGestionadaCommand>().Should().ContainSingle()
            .Which.Should().Be(new MarcarDocumentacionGestionadaCommand(VisitaId, detalle.Version)));
        cut.WaitForAssertion(() => Cabecera(cut).Should().Contain("Documentación gestionada"));
    }

    /// <summary>
    /// El compositor no envía con el comando genérico de Comunicaciones: la página le pasa el
    /// suyo, que lleva la Visita y la versión con la que se preparó el paquete.
    /// </summary>
    [Fact]
    public async Task El_compositor_del_paquete_envia_por_el_comando_de_la_Visita_con_su_Id_y_su_version()
    {
        var detalle = Detalle();
        var (cut, mediador) = Montar(detalle, documentacion: DocumentacionConIncidencias());
        cut.FindAll("[data-pieza=cabecera-identidad] button").Single(b => b.TextContent.Contains("Enviar por correo")).Click();

        var compositor = cut.FindComponent<Stub<RedactarMensajeDrawer>>().Instance.Parameters;
        var enviarCon = compositor.Get(x => x.EnviarCon);
        enviarCon.Should().NotBeNull("sin él, el paquete saldría por el envío genérico y la Visita seguiría por gestionar");

        var adjunto = new AdjuntoParaEnviarDto("paquete.zip", "application/zip", [1, 2, 3]);
        var conexion = Guid.NewGuid();
        var resultado = await cut.InvokeAsync(() => enviarCon!(
            new EnviarMensajeNuevoCommand(conexion, ["accesos@centro.example"], "Solicitud", "<p>Hola</p>", Adjuntos: [adjunto])));

        resultado.EsExitoso.Should().BeTrue();
        var enviado = mediador.Enviadas.OfType<EnviarPaqueteAcreditacionVisitaCommand>().Should().ContainSingle().Subject;
        (enviado.VisitaId, enviado.VersionVisita, enviado.ConexionIntegracionId).Should().Be((VisitaId, detalle.Version, conexion));
        enviado.Adjuntos.Should().ContainSingle().Which.Should().BeSameAs(adjunto);
        mediador.Enviadas.OfType<EnviarMensajeNuevoCommand>().Should().BeEmpty();
    }

    [Fact]
    public void Una_respuesta_tardia_de_otra_visita_no_pinta_la_visita_equivocada()
    {
        var otra = Guid.NewGuid();
        var mediador = new MediatorFalso();
        mediador.DetallesDiferidos[VisitaId] = new TaskCompletionSource<DetalleVisitaDto?>();
        mediador.Detalles[otra] = Detalle(otra, "Planta Norte", requiereGestion: false, porCorreo: false);
        this.ConRolDeEscritura();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddLocalization();

        var cut = Render<VisitaDetalle>(p => p.Add(x => x.VisitaId, VisitaId));
        cut.Render(p => p.Add(x => x.VisitaId, otra));
        mediador.DetallesDiferidos[VisitaId].SetResult(Detalle(centro: "Sede Sevilla"));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Be("Planta Norte"));
    }
}
