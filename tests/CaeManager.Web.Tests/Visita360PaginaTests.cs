using CaeManager.Web.Components.Layout;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Visitas.Commands.EnviarPaqueteAcreditacionVisita;
using CaeManager.Application.Visitas.Commands.MarcarDocumentacionGestionada;
using Microsoft.AspNetCore.Components.Web;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.EditarContactoVisita;
using CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;
using CaeManager.Application.Visitas.Commands.QuitarMarcaDocumentacionGestionada;
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
public partial class Visita360PaginaTests : BunitContext
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
                case ObtenerDocumentoPorIdQuery:
                    return null;
                case ObtenerTrabajadoresParaSelectorQuery:
                    return Documentacion.Trabajadores.Select(t => new TrabajadorSelectorDto(t.TrabajadorId, t.NombreCompleto, null, t.EmpleadorNombre)).ToList();
                case ObtenerEmpresasParaSelectorQuery:
                    return new List<EmpresaSelectorDto> { new(Documentacion.EmpresaId, "Empresa del Centro") };
                case ObtenerTiposDocumentoQuery q:
                    var documentos = q.AmbitoAplicacion == AmbitoAplicacion.Empresa
                        ? Documentacion.Empresa.Documentos
                        : Documentacion.Trabajadores.SelectMany(t => t.Documentacion.Documentos).ToList();
                    return documentos.DistinctBy(d => d.TipoDocumentoId).Select(d => new TipoDocumentoListaDto(
                        d.TipoDocumentoId, d.TipoDocumentoNombre, 12, true, 0, q.AmbitoAplicacion ?? AmbitoAplicacion.Trabajador,
                        default, default, null, null, null, null, false, false, false, default, [])).ToList();
                case ObtenerSolicitudAccesoCorreoQuery:
                    return Result.Exito(new SolicitudAccesoCorreoDto("accesos@centro.example", "Solicitud de acceso — Sede Sevilla", "Buenos días"));
                case ObtenerAvisoVisitaQuery:
                    return Result.Exito(new AvisoVisitaDto("Aviso de visita — Sede Sevilla", "Mañana acuden dos técnicos."));
                case ObtenerPaqueteDocumentalVisitaQuery:
                    return Result.Exito(new PaqueteDocumentalDescargaDto("paquete.zip", [1, 2, 3]));
                case QuitarMarcaDocumentacionGestionadaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { DocumentacionGestionadaEnUtc = null, Version = Guid.NewGuid() };
                    return Result.Exito();
                case MarcarDocumentacionGestionadaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { DocumentacionGestionadaEnUtc = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), Version = Guid.NewGuid() };
                    return Result.Exito();
                case EnviarPaqueteAcreditacionVisitaCommand c:
                    Detalles[c.VisitaId] = Detalles[c.VisitaId] with { DocumentacionGestionadaEnUtc = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc), Version = Guid.NewGuid() };
                    return Result.Exito(Guid.NewGuid());
                case MarcarNotificadoClienteCommand c:
                    // Como el servidor: cualquier guardado de la Visita renueva su versión.
                    Detalles[c.Id] = Detalles[c.Id] with { NotificadoCliente = c.Notificado, Version = Guid.NewGuid() };
                    return Result.Exito();
                case CancelarVisitaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { EstaCancelada = true, MotivoCancelacion = c.Motivo };
                    return Result.Exito(new VisitaCanceladaDto(c.Id, Guid.NewGuid()));
                case EditarContactoVisitaCommand c:
                    Detalles[c.Id] = Detalles[c.Id] with { ContactoNombre = c.Nombre, ContactoTelefono = c.Telefono, ContactoCorreo = c.Correo, Version = Guid.NewGuid() };
                    return Result.Exito();
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

    private sealed class AlmacenArchivosNoUsado : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class ConversorNoUsado : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static DetalleVisitaDto Detalle(
        Guid? id = null, string centro = "Sede Sevilla", bool requiereGestion = true, bool porCorreo = true, bool cancelada = false,
        TramoAntelacion? tramo = null) => new(
        id ?? VisitaId, centro, "Titular Demo S.A.", Guid.NewGuid(), "Contratista Demo S.L.", Hoy, Hoy,
        Notas: "Puerta de servicio.", NotificadoCliente: false,
        Trabajadores: [new(Paula, "Paula Campos"), new(Diego, "Diego Ruiz")],
        HoraEstimadaAcceso: new TimeOnly(8, 0), FechaHoraSolicitudUtc: null, FechaHoraExpedienteCompletoUtc: null,
        AntelacionNominalHoras: tramo is null ? null : 15, AntelacionEfectivaHoras: tramo is null ? null : 15, Tramo: tramo, AtribucionUrgencia.SinUrgencia,
        CentroRequiereGestionCae: requiereGestion, CentroGestionadoPorCorreo: porCorreo,
        EstaCancelada: cancelada, MotivoCancelacion: cancelada ? "Aplazada" : null, Version: Guid.NewGuid(),
        CentroId: Guid.NewGuid(), EmpresaTitularId: Guid.NewGuid(), Origen: OrigenVisita.Correo, NivelUrgencia: NivelUrgenciaVisita.Normal,
        NotaInternaVisible: true);

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
        DetalleVisitaDto? detalle, string rol = Roles.GestorCae, DocumentacionVisitaDto? documentacion = null, string? pestana = null)
    {
        var mediador = new MediatorFalso();
        if (detalle is not null)
            mediador.Detalles[detalle.Id] = detalle;
        if (documentacion is not null)
            mediador.Documentacion = documentacion;

        this.ConRolDeEscritura(rol);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosNoUsado>();
        Services.AddScoped<IConversorWordPdfService, ConversorNoUsado>();
        Services.AddLogging();
        Services.AddLocalization();
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        // La pestaña es un parámetro de consulta: llega por la URL, no como parámetro del componente.
        if (pestana is not null)
            Services.GetRequiredService<NavigationManager>().NavigateTo($"visitas/{detalle?.Id ?? VisitaId}?pestana={pestana}");

        var cut = Render<VisitaDetalle>(p => p.Add(x => x.VisitaId, detalle?.Id ?? VisitaId));
        return (cut, mediador);
    }

    private static IEnumerable<string> Botones(IRenderedComponent<VisitaDetalle> cut) =>
        cut.FindAll("button, a").Select(b => b.TextContent.Trim());

    private static AngleSharp.Dom.IElement FilaDeDocumento(IRenderedComponent<VisitaDetalle> cut, string tipo) =>
        cut.FindAll("[data-pieza=fila]").Single(f => f.QuerySelector(".fila-relacion-nombre")?.TextContent.Trim() == tipo);

    [Theory]
    [InlineData(EstadoDocumento.Vencido, "Gestionar")]
    [InlineData(EstadoDocumento.EnTolerancia, "Gestionar")]
    [InlineData(EstadoDocumento.Urgente, "Gestionar")]
    [InlineData(EstadoDocumento.SinConfirmar, "Gestionar")]
    public async Task Renovar_y_Confirmar_inician_el_editor_del_documento_correcto_sin_abrir_el_visor(EstadoDocumento estado, string accion)
    {
        var documento = Documento("Documento a corregir", estado, Paula);
        var documentacion = new DocumentacionVisitaDto(Guid.NewGuid(), new(EstadoDocumento.Vigente, []),
            [new(Paula, "Paula Campos", "Empresa Demo", new(estado, [documento]))]);
        var (cut, mediador) = Montar(Detalle(porCorreo: false), documentacion: documentacion);
        var fila = FilaDeDocumento(cut, documento.TipoDocumentoNombre);
        fila.QuerySelector(".fila-relacion-acciones")!.TextContent.Trim().Should().Be(accion);

        await fila.QuerySelector(".fila-relacion-acciones button")!.ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(documento.DocumentoId!.Value);
        cut.FindComponent<Stub<VisorDocumento>>().Instance.Parameters.Get(x => x.Visible).Should().BeFalse();
        // El falso devuelve null por ID: aquí se observa la entrada al editor, no un guardado completo.
    }

    /// <summary>
    /// Regla 8 de LEEME-AUTORIDAD (2026-10-18): una sola acción, «Gestionar», en la fila de un documento que pide trabajo;
    /// Consulta y Cliente no ven acción de escritura; un documento Vigente también lleva «Gestionar», en tono secundario
    /// (regla de Chris, 2026-10-18).
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador, EstadoDocumento.Vencido, "Gestionar")]
    [InlineData(Roles.DireccionCae, EstadoDocumento.Faltante, "Gestionar")]
    [InlineData(Roles.CoordinadorCae, EstadoDocumento.SinConfirmar, "Gestionar")]
    [InlineData(Roles.GestorCae, EstadoDocumento.EnTolerancia, "Gestionar")]
    [InlineData(Roles.GestorCae, EstadoDocumento.Vigente, "Gestionar")]
    [InlineData(Roles.Consulta, EstadoDocumento.Vigente, null)]
    [InlineData(Roles.Consulta, EstadoDocumento.Vencido, null)]
    [InlineData(Roles.Consulta, EstadoDocumento.Proximo, null)]
    [InlineData(Roles.Cliente, EstadoDocumento.Faltante, null)]
    public void La_fila_del_documento_ofrece_solo_Gestionar_segun_rol_y_estado(string rol, EstadoDocumento estado, string? esperado)
    {
        var documento = Documento("Documento de la visita", estado, Paula);
        var documentacion = new DocumentacionVisitaDto(Guid.NewGuid(), new(EstadoDocumento.Vigente, []),
            [new(Paula, "Paula Campos", "Empresa Demo", new(estado, [documento]))]);
        var (cut, _) = Montar(Detalle(porCorreo: false), rol, documentacion);
        var botones = FilaDeDocumento(cut, documento.TipoDocumentoNombre)
            .QuerySelectorAll(".fila-relacion-acciones button").Select(b => b.TextContent.Trim()).ToList();
        if (esperado is null)
            botones.Should().BeEmpty();
        else
        {
            botones.Should().Equal(esperado);
            FilaDeDocumento(cut, documento.TipoDocumentoNombre).QuerySelector(".fila-relacion-acciones button")!.ClassList
                .Contains("gestionar-discreto").Should().Be(estado == EstadoDocumento.Vigente,
                    "solo el documento al día lleva «Gestionar» en tono secundario");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Subir_preselecciona_el_Tipo_y_el_Trabajador_o_la_Empresa_del_Centro(bool deEmpresa)
    {
        var documento = Documento("Documento pendiente", EstadoDocumento.Faltante, deEmpresa ? null : Paula);
        var empresa = Guid.NewGuid();
        var documentacion = new DocumentacionVisitaDto(empresa,
            new(deEmpresa ? EstadoDocumento.Faltante : EstadoDocumento.Vigente, deEmpresa ? [documento] : []),
            [new(Paula, "Paula Campos", "Empresa Demo", new(deEmpresa ? EstadoDocumento.Vigente : EstadoDocumento.Faltante, deEmpresa ? [] : [documento]))]);
        var (cut, mediador) = Montar(Detalle(porCorreo: false), documentacion: documentacion);
        var fila = FilaDeDocumento(cut, documento.TipoDocumentoNombre);
        fila.QuerySelector(".fila-relacion-acciones")!.TextContent.Trim().Should().Be("Gestionar");

        await fila.QuerySelector(".fila-relacion-acciones button")!.ClickAsync(new MouseEventArgs());
        cut.FindComponent<SelectorTipoDocumento>().Instance.Valor.Should().Be(documento.TipoDocumentoId.ToString());
        if (deEmpresa)
            cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Empresa").Instance.Valor.Should().Be(empresa.ToString());
        else
            cut.FindComponents<CampoBuscarSelect>().Single(c => c.Instance.Etiqueta == "Trabajador").Instance.Valor.Should().Be(Paula.ToString());
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerTrabajadoresParaSelectorQuery>().Should().ContainSingle().Which.Alcance.Should().Be(AlcanceSelectorTrabajadores.Cartera);
        cut.FindComponent<Stub<VisorDocumento>>().Instance.Parameters.Get(x => x.Visible).Should().BeFalse();
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    public void Un_documento_al_dia_lleva_Gestionar_en_tono_secundario(EstadoDocumento estado)
    {
        var documento = Documento("Documento al día", estado, Paula);
        var documentacion = new DocumentacionVisitaDto(Guid.NewGuid(), new(EstadoDocumento.Vigente, []),
            [new(Paula, "Paula Campos", "Empresa Demo", new(estado, [documento]))]);
        var (cut, _) = Montar(Detalle(porCorreo: false), documentacion: documentacion);
        var botones = FilaDeDocumento(cut, documento.TipoDocumentoNombre).QuerySelectorAll(".fila-relacion-acciones button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Gestionar");
        botones[0].ClassList.Should().Contain("gestionar-discreto");
    }

    [Fact]
    public async Task Un_documento_Por_vencer_aunque_no_sea_urgente_lleva_Gestionar_y_abre_su_editor()
    {
        var documento = Documento("Documento por vencer", EstadoDocumento.Proximo, Paula);
        var documentacion = new DocumentacionVisitaDto(Guid.NewGuid(), new(EstadoDocumento.Vigente, []),
            [new(Paula, "Paula Campos", "Empresa Demo", new(EstadoDocumento.Proximo, [documento]))]);
        var (cut, mediador) = Montar(Detalle(porCorreo: false), documentacion: documentacion);
        var fila = FilaDeDocumento(cut, documento.TipoDocumentoNombre);
        fila.QuerySelectorAll(".fila-relacion-acciones button").Select(b => b.TextContent.Trim()).Should().Equal("Gestionar");

        await fila.QuerySelector(".fila-relacion-acciones button")!.ClickAsync(new MouseEventArgs());
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle().Which.Id.Should().Be(documento.DocumentoId!.Value);
    }

    [Fact]
    public async Task Guardar_en_el_editor_relee_la_comprobacion_y_retira_la_accion_resuelta()
    {
        var documentacion = DocumentacionConIncidencias();
        var (cut, mediador) = Montar(Detalle(porCorreo: false), documentacion: documentacion);
        var consultasDetalle = mediador.Enviadas.OfType<ObtenerDetalleVisitaQuery>().Count();
        var anteriores = mediador.Enviadas.OfType<ObtenerDocumentacionVisitaQuery>().Count();
        var trabajador = documentacion.Trabajadores[0];
        mediador.Documentacion = documentacion with
        {
            Trabajadores = [trabajador with { Documentacion = new(EstadoDocumento.Vigente, trabajador.Documentacion.Documentos.Select(d => d with { Estado = EstadoDocumento.Vigente }).ToList()) }, documentacion.Trabajadores[1]]
        };

        await cut.InvokeAsync(() => cut.FindComponent<DrawerGestionDocumento>().Instance.OnGuardado.InvokeAsync());
        mediador.Enviadas.OfType<ObtenerDocumentacionVisitaQuery>().Should().HaveCount(anteriores + 1);
        mediador.Enviadas.OfType<ObtenerDetalleVisitaQuery>().Should().HaveCount(consultasDetalle);
        // Resuelta la incidencia, el documento queda al día: su «Gestionar» pasa a tono secundario.
        FilaDeDocumento(cut, "Entrega de EPI").QuerySelectorAll(".fila-relacion-acciones button").Should().ContainSingle()
            .Which.ClassList.Should().Contain("gestionar-discreto");
    }

    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void Consulta_y_Visita_cancelada_no_montan_el_editor_ni_ofrecen_acciones_documentales(string rol, bool cancelada)
    {
        var (cut, mediador) = Montar(Detalle(cancelada: cancelada), rol, DocumentacionConIncidencias());
        cut.FindComponents<DrawerGestionDocumento>().Should().BeEmpty();
        cut.FindAll(".fila-relacion-acciones button").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerTiposDocumentoQuery>().Should().BeEmpty();
    }

    [Theory]
    [InlineData(Roles.GestorCae, false, true)]
    [InlineData(Roles.Consulta, false, false)]
    [InlineData(Roles.GestorCae, true, false)]
    public void La_nota_interna_se_lee_una_vez_en_el_lateral_y_conserva_la_puerta_de_edicion(string rol, bool cancelada, bool puedeEditar)
    {
        var (cut, _) = Montar(Detalle(cancelada: cancelada), rol);

        cut.FindAll("[data-pieza=nota-interna]").Should().ContainSingle();
        var lateral = cut.Find("[data-pieza=lateral]");
        lateral.QuerySelectorAll(".tarjeta-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Información", "Nota interna");
        var nota = cut.Find("[data-pieza=lateral] [data-pieza=nota-interna]");
        nota.TextContent.Should().Contain("Puerta de servicio.").And.Contain("No la ve el Cliente.");
        nota.QuerySelectorAll("button").Should().HaveCount(puedeEditar ? 1 : 0);
        if (puedeEditar)
        {
            nota.QuerySelector("button")!.Click();
            cut.FindComponent<Stub<EditarVisitaDrawer>>().Instance.Parameters.Get(p => p.Visible).Should().BeTrue();
            cut.FindComponent<Stub<EditarVisitaDrawer>>().Instance.Parameters.Get(p => p.VisitaId).Should().Be(VisitaId);
        }
    }

    [Fact]
    public void La_nota_interna_vacia_muestra_el_estado_compartido()
    {
        var (cut, _) = Montar(Detalle() with { Notas = null });

        cut.Find("[data-pieza=lateral] [data-pieza=nota-interna] [data-vacia=true]")
            .TextContent.Should().Be("Sin nota interna.");
    }

    [Theory]
    [InlineData(Roles.Consulta, false)]
    [InlineData(Roles.GestorCae, true)]
    public void La_nota_vacia_conserva_su_visibilidad_original(string rol, bool cancelada)
    {
        var (cut, _) = Montar(Detalle(cancelada: cancelada) with { Notas = null }, rol);

        cut.FindAll("[data-pieza=nota-interna]").Should().BeEmpty();
    }

    // ── Pestaña «Ficha» ───────────────────────────────────────────────────
    // Decisión del 2026-10-09: el lateral lleva dos o tres cajas como mucho; «Antelación» vive en «Ficha».

    [Fact]
    public void Con_antelacion_la_ficha_va_la_ultima_y_el_lateral_ya_no_la_lleva()
    {
        var (cut, _) = Montar(Detalle(tramo: TramoAntelacion.Urgente), documentacion: DocumentacionConIncidencias());

        var pestanas = cut.FindAll("[role=tab]");
        pestanas.Should().HaveCount(3);
        pestanas[2].TextContent.Trim().Should().Be("Ficha");
        cut.Find("[data-pieza=lateral]").QuerySelectorAll(".tarjeta-titulo").Select(t => t.TextContent.Trim())
            .Should().NotContain("Antelación").And.HaveCountLessThanOrEqualTo(3);
        cut.FindAll("[data-pieza=cajas-ficha]").Should().BeEmpty("sin pedirla, la visita abre en su primera pestaña");
    }

    [Fact]
    public void La_pestana_ficha_llega_por_la_url_y_lleva_la_antelacion()
    {
        var (cut, _) = Montar(Detalle(tramo: TramoAntelacion.Urgente), documentacion: DocumentacionConIncidencias(), pestana: "ficha");

        cut.FindAll("[role=tab]")[^1].GetAttribute("aria-selected").Should().Be("true");
        var cajas = cut.FindAll("[data-pieza=cajas-ficha] > [data-caja]");
        cajas.Select(c => c.GetAttribute("data-caja")).Should().Equal("antelacion");
        cajas[0].QuerySelector(".tarjeta-titulo")!.TextContent.Trim().Should().Be("Antelación");
        cajas[0].TextContent.Should().Contain("15");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Sin_antelacion_que_ensenar_no_hay_pestana_ficha_y_pedirla_abre_la_primera(bool cancelada)
    {
        // Sin tramo calculado, o cancelada aunque lo tenga: la pestaña quedaría vacía y no se pinta.
        var detalle = Detalle(cancelada: cancelada, tramo: cancelada ? TramoAntelacion.Urgente : null);
        var (cut, _) = Montar(detalle, documentacion: DocumentacionConIncidencias(), pestana: "ficha");

        cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).Should().NotContain("Ficha");
        cut.FindAll("[data-pieza=cajas-ficha]").Should().BeEmpty();
        cut.Markup.Should().NotContain("Antelación");
    }

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
    public void Visita_no_encontrada_o_fuera_de_alcance_pinta_FichaNoEncontrada_sin_Reintentar()
    {
        ComponentFactories.AddStub<FichaNoEncontrada>();
        var (cut, _) = Montar(detalle: null);

        cut.RutaListaDeFichaNoEncontrada().Should().Be("/visitas");
        cut.Markup.Should().NotContain("Reintentar").And.NotContain("No pudimos cargar esta visita");
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

    /// <summary>
    /// Decisión de producto del 2026-10-09: la cabecera no repite en una banda lo que la comprobación previa ya
    /// dice. Cada documento vencido o sin presentar se ve —y se abre— en su fila.
    /// </summary>
    [Fact]
    public void Con_incidencias_no_hay_banda_y_cada_una_esta_en_su_fila()
    {
        var (cut, _) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());

        cut.FindAll("[data-pieza=banda]").Should().BeEmpty();
        var filas = cut.FindAll("[data-pieza=fila][data-tono=peligro] button.fila-relacion-nombre");
        filas.Select(f => f.TextContent.Trim()).Should().Contain(["Aptitud médica", "Entrega de EPI"]);

        // Lo que hacía la incidencia de la banda: la fila del documento sin presentar lleva a subirlo.
        filas.Single(f => f.TextContent.Trim() == "Entrega de EPI").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"/documentos?trabajadorId={Paula}&tipoDocumentoId=");
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

    [Fact]
    public async Task Vacio_filtrado_tras_actualizar_documentacion_ofrece_Quitar_filtros_en_la_nota()
    {
        var (cut, mediador) = Montar(Detalle(), documentacion: DocumentacionConIncidencias());
        cut.FindAll(".filtro-estados-opcion").Single(b => b.TextContent.StartsWith("Vencido", StringComparison.Ordinal)).Click();
        mediador.Documentacion = new(Guid.NewGuid(), new(EstadoDocumento.Vigente, [Documento("RNT", EstadoDocumento.Vigente)]),
            [new(Paula, "Paula Campos", "Contratista Demo S.L.", new(EstadoDocumento.Vigente, [Documento("Aptitud médica", EstadoDocumento.Vigente, Paula)]))]);
        await cut.InvokeAsync(() => cut.FindComponent<DrawerGestionDocumento>().Instance.OnGuardado.InvokeAsync());
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza=fila]").Should().BeEmpty());
        cut.Find(".lista-relaciones-pie").TextContent.Should().Contain("Ninguno con ese filtro.");
        cut.Find(".lista-relaciones-pie button").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza=fila]").Count.Should().Be(2));
        cut.FindAll(".filtro-estados-opcion[aria-pressed=true]").Should().ContainSingle().Which.TextContent.Should().StartWith("Todos");
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

    /// <summary>Deshacer: gestionada, el menú ofrece quitar la marca; al quitarla la cabecera vuelve a «por gestionar».</summary>
    [Fact]
    public async Task Quitar_la_marca_desde_el_menu_manda_el_comando_con_la_version_y_la_cabecera_vuelve_a_por_gestionar()
    {
        var gestionada = Detalle() with { DocumentacionGestionadaEnUtc = new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc) };
        var (cut, mediador) = Montar(gestionada, documentacion: DocumentacionConIncidencias());

        await cut.Find("[data-pieza=cabecera-identidad] .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Quitar la marca de gestionada").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<QuitarMarcaDocumentacionGestionadaCommand>().Should().ContainSingle()
            .Which.Should().Be(new QuitarMarcaDocumentacionGestionadaCommand(VisitaId, gestionada.Version)));
        cut.WaitForAssertion(() => Cabecera(cut).Should().Contain("Documentación por gestionar"));
    }

    [Fact]
    public void En_un_Centro_sin_gestion_CAE_la_cabecera_no_habla_de_documentacion_por_gestionar()
    {
        var (cut, _) = Montar(Detalle(requiereGestion: false, porCorreo: false));

        Cabecera(cut).Should().NotContain("por gestionar").And.NotContain("Documentación gestionada");
        cut.Markup.Should().NotContain("Marcar documentación gestionada");
        cut.Markup.Should().NotContain("Quitar la marca de gestionada");
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
    /// Marcar «Avisada» renueva la versión de la Visita. Si la página se quedara con la anterior,
    /// la marca siguiente chocaría «porque otra persona la modificó» sin que nadie la tocara.
    /// </summary>
    [Fact]
    public async Task Tras_marcar_Avisada_la_marca_de_gestionada_viaja_con_la_version_nueva()
    {
        var detalle = Detalle();
        var (cut, mediador) = Montar(detalle, documentacion: DocumentacionConIncidencias());

        await cut.Find("[data-pieza=cabecera-identidad] .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Marcar como notificada").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<MarcarNotificadoClienteCommand>().Should().ContainSingle());
        var versionTrasAvisar = mediador.Detalles[VisitaId].Version;
        versionTrasAvisar.Should().NotBe(detalle.Version, "control: «Avisada» renovó la versión");

        await cut.Find("[data-pieza=cabecera-identidad] .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Marcar documentación gestionada").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<MarcarDocumentacionGestionadaCommand>().Should().ContainSingle()
            .Which.Version.Should().Be(versionTrasAvisar));
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
