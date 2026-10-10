using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Vehiculos.Commands.GuardarNotaInternaVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Vehículo 360, página (<see cref="VehiculoDetalle"/>) contra su mockup
/// «Vehiculo 360 página TALVEG.dc.html», primer incremento: identidad con
/// anillo de documentos registrados al día, sin banda de incidencias,
/// pestañas Documentación e Historial con la activa en <c>?pestana=</c>,
/// lateral Información, y lo que pierde el rol Consulta. Prueban efectos (qué
/// se ve, qué consultas salen, qué panel queda abierto); bUnit no evalúa CSS.
///
/// <para>
/// <c>PestanaHistorial</c> es el mismo componente del panel, con sus propios
/// tests: aquí va en stub y se comprueba con qué entidad se monta.
/// <c>DrawerGestionDocumento</c> se monta de verdad (la página lo guarda por
/// <c>@ref</c>), pero ningún test lo abre: su alta tiene sus tests.
/// </para>
/// </summary>
public class Vehiculo360PaginaTests : BunitContext
{
    private static readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    /// <summary>BotonCopiar importa ./js/clipboard.js; Pestanas enfoca por interop.</summary>
    public Vehiculo360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        ComponentFactories.AddStub<PestanaHistorial>();
    }

    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, VehiculoDetalleDto> Detalles { get; } = [];
        public List<DocumentoListaDto> Documentos { get; } = [];

        /// <summary>Total que declara la consulta; null = los que devuelve.</summary>
        public int? TotalDocumentos { get; set; }

        public bool FallaDocumentos { get; set; }

        /// <summary>Si está, la consulta de detalle de ese Vehículo no responde hasta que se complete.</summary>
        public Dictionary<Guid, TaskCompletionSource> Compuertas { get; } = [];
        public List<object> Enviadas { get; } = [];

        /// <summary>Lo que responde el servidor a «Guardar» de la nota, y la ficha que entrega la relectura posterior.</summary>
        public Result ResultadoNota { get; set; } = Result.Exito();
        public VehiculoDetalleDto? DetalleTrasGuardarNota { get; set; }
        public Exception? ExcepcionAlGuardarNota { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return request is ObtenerVehiculoPorIdQuery q && Compuertas.TryGetValue(q.Id, out var compuerta)
                ? ResponderTrasAsync<TResponse>(compuerta.Task, request)
                : Task.FromResult((TResponse)Responder(request)!);
        }

        private async Task<TResponse> ResponderTrasAsync<TResponse>(Task compuerta, object request)
        {
            await compuerta;
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerVehiculoPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            GuardarNotaInternaVehiculoCommand => ResponderGuardarNota(),
            ObtenerDocumentosQuery when FallaDocumentos => throw new InvalidOperationException("fallo simulado"),
            ObtenerDocumentosQuery => new ResultadoPaginado<DocumentoListaDto>(
                Documentos.ToList(), TotalDocumentos ?? Documentos.Count, 1, 50),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        private Result ResponderGuardarNota()
        {
            if (ExcepcionAlGuardarNota is { } excepcion)
                throw excepcion;
            if (ResultadoNota.EsExitoso && DetalleTrasGuardarNota is { } tras)
                Detalles[tras.Id] = tras;
            return ResultadoNota;
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

    /// <summary>La página monta DrawerGestionDocumento, que los inyecta; ningún test lo abre.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Ningún test de la ficha abre archivos; si esto salta, la página cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Ningún test de la ficha convierte nada; si esto salta, la página cambió de camino.");
    }

    private MediatorFalso Registrar(MediatorFalso mediador, string rol = Roles.GestorCae)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddLogging();
        Services.AddLocalization();
        this.ConRolDeEscritura(rol);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<VehiculoDetalle> Renderizar(Guid id, string? consulta = null)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"vehiculos/{id}{consulta}");
        var cut = Render<VehiculoDetalle>(p => p.Add(x => x.VehiculoId, id));
        cut.WaitForAssertion(() => cut.FindAll(".cabecera-identidad, .estado-vacio").Should().NotBeEmpty());
        return cut;
    }

    private static DocumentoListaDto Documento(string tipo, EstadoDocumento estado, DateOnly? vence) =>
        new(Guid.NewGuid(), AmbitoAplicacion.Vehiculo, "Camión grúa", tipo, Hoy.AddDays(-350), vence, estado, null, []);

    /// <summary>
    /// El «Camión grúa» de la maqueta: ITV vencida hace 36 días, seguro por
    /// vencer, ficha técnica sin confirmar y permiso que no caduca. 2 de 4.
    /// </summary>
    private static (Guid Id, MediatorFalso Mediador) CamionGrua(Guid? empresaId = null, bool empleadorEsEmpresa = true)
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso();
        mediador.Detalles[id] = new VehiculoDetalleDto(
            id,
            empleadorEsEmpresa ? empresaId ?? Guid.NewGuid() : null,
            empleadorEsEmpresa ? null : Guid.NewGuid(),
            "Montajes Skynet S.L.", "Camión grúa", "Iveco Daily", "9012 GHI", Guid.NewGuid(),
            new FraccionCumplimiento(AlDia: 2, Requeridos: 4), EstadoDocumento.Vencido, Notas: null, NotaInternaVisible: true);
        mediador.Documentos.AddRange(
        [
            Documento("ITV", EstadoDocumento.Vencido, Hoy.AddDays(-36)),
            Documento("Seguro", EstadoDocumento.Urgente, Hoy.AddDays(11)),
            Documento("Ficha técnica", EstadoDocumento.SinConfirmar, null),
            Documento("Permiso de circulación", EstadoDocumento.SinCaducidad, null)
        ]);
        return (id, mediador);
    }

    private static (Guid Id, MediatorFalso Mediador) RecienCreado()
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso();
        mediador.Detalles[id] = new VehiculoDetalleDto(
            id, Guid.NewGuid(), null, "Montajes Skynet S.L.", "Furgoneta nueva", "Transit", "0001 AAA", Guid.NewGuid(),
            FraccionCumplimiento.SinRequisitos, null, Notas: null, NotaInternaVisible: true);
        return (id, mediador);
    }

    private static List<string> Textos(IEnumerable<IElement> elementos) =>
        elementos.Select(e => e.TextContent.Trim()).ToList();

    private static string SinEspaciosDeMas(string texto) =>
        string.Join(' ', texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ── Identidad ─────────────────────────────────────────────────────────

    [Fact]
    public void La_identidad_dice_Vehiculo_nombre_matricula_copiable_modelo_y_empleador_enlazado()
    {
        var empresaId = Guid.NewGuid();
        var (id, mediador) = CamionGrua(empresaId);
        Registrar(mediador);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad");
        cabecera.GetAttribute("data-pieza").Should().Be("cabecera-identidad");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Camión grúa");
        cabecera.TextContent.Should().Contain("Vehículo").And.Contain("Iveco Daily").And.Contain("Empleador:");

        var placa = cabecera.QuerySelector(".vehiculo360-placa button");
        placa.Should().NotBeNull("la matrícula se copia al pulsarla");
        placa!.TextContent.Trim().Should().Be("9012 GHI");
        placa.GetAttribute("aria-label").Should().Be("Copiar la matrícula 9012 GHI");

        var empleador = cabecera.QuerySelector("a.vehiculo360-enlace");
        empleador.Should().NotBeNull();
        empleador!.TextContent.Trim().Should().Be("Montajes Skynet S.L.");
        empleador.GetAttribute("href").Should().Be($"/empresas/{empresaId}");
    }

    [Fact]
    public void El_empleador_que_no_es_Empresa_se_nombra_sin_enlace()
    {
        var (id, mediador) = CamionGrua(empleadorEsEmpresa: false);
        Registrar(mediador);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad");
        cabecera.TextContent.Should().Contain("Montajes Skynet S.L.");
        cabecera.QuerySelectorAll("a.vehiculo360-enlace").Should().BeEmpty(
            "solo la Empresa tiene hoy ficha 360 con ruta propia: un enlace a /empresas/{id} con otro id daría «no encontrado»");
    }

    [Fact]
    public void El_anillo_pinta_el_porcentaje_y_la_fraccion_de_documentos_registrados_al_dia()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);

        var cut = Renderizar(id);

        var anillo = cut.Find(".cabecera-identidad [data-pieza='anillo']");
        anillo.ClassList.Should().Contain("anillo-cumplimiento-cabecera").And.Contain("anillo-cumplimiento-advertencia");
        SinEspaciosDeMas(anillo.QuerySelector(".anillo-cumplimiento-texto")!.TextContent).Should().Be("50% 2 de 4");
        anillo.GetAttribute("aria-label").Should().Be("2 de 4 documentos registrados al día");
    }

    // ── Sin banda de incidencias ──────────────────────────────────────────

    /// <summary>
    /// Decisión de producto del 2026-10-09: la cabecera no repite en una banda lo que la lista ya
    /// dice. Lo que la banda dejaba hacer —abrir el documento vencido, que es donde se renueva—
    /// sigue en la fila de ese documento.
    /// </summary>
    [Fact]
    public async Task Con_documentos_vencidos_no_hay_banda_y_la_fila_del_vencido_abre_su_documento()
    {
        var (id, mediador) = CamionGrua();
        mediador.Documentos[1] = Documento("Seguro", EstadoDocumento.Vencido, Hoy.AddDays(-1));
        Registrar(mediador);
        var itv = mediador.Documentos[0];

        var cut = Renderizar(id);

        cut.FindAll("[data-pieza='banda']").Should().BeEmpty("los vencidos ya se ven en su fila");
        var vencidas = cut.FindAll("[data-pieza='fila'][data-tono='peligro']");
        Textos(vencidas.Select(f => f.QuerySelector(".fila-relacion-nombre")!)).Should().Equal(["ITV", "Seguro"]);

        await vencidas[0].QuerySelector("button.fila-relacion-nombre")!.ClickAsync(new());

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(
            new WorkspaceFrame(EntidadWorkspace.Documento, itv.Id, "ITV", "informacion"),
            "la fila abre el panel del Documento, que es donde hoy se renueva");
    }

    // ── Documentación ─────────────────────────────────────────────────────

    [Fact]
    public void La_lista_pinta_cada_documento_con_su_vencimiento_y_su_estado_y_tine_solo_el_vencido()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);

        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));

        var filas = cut.FindAll("[data-pieza='fila']");
        Textos(filas.Select(f => f.QuerySelector(".fila-relacion-nombre")!)).Should().Equal(
            ["ITV", "Seguro", "Ficha técnica", "Permiso de circulación"]);
        filas[0].TextContent.Should().Contain($"Vence: {Hoy.AddDays(-36):dd/MM/yyyy}");
        filas[2].TextContent.Should().Contain("Vence: —");
        filas.Select(f => f.GetAttribute("data-tono")).Should().Equal(["peligro", null, null, null]);
        filas.Should().OnlyContain(f => f.QuerySelector(".fila-relacion-estado .badge") != null);

        var tarjeta = cut.Find(".lista-relaciones-tarjeta");
        tarjeta.TextContent.Should().Contain("Mostrando 4 de 4")
            .And.Contain("Solo aparecen documentos registrados");
        SinEspaciosDeMas(cut.Find(".pestanas-boton-activa .pestanas-contador").TextContent).Should().Be("4 documentos");
    }

    [Fact]
    public void Si_hay_mas_documentos_de_los_que_se_pintan_el_encabezado_lo_dice()
    {
        var (id, mediador) = CamionGrua();
        mediador.TotalDocumentos = 73;
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => cut.Find(".lista-relaciones-tarjeta").TextContent.Should().Contain("Mostrando 4 de 73"));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Ambito = AmbitoAplicacion.Vehiculo, PropietarioId = (Guid?)id, TamanoPagina = 50 });
    }

    [Fact]
    public async Task El_nombre_del_documento_abre_su_panel()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);
        var seguro = mediador.Documentos[1];
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));

        await cut.FindAll("[data-pieza='fila']")[1].QuerySelector("button.fila-relacion-nombre, .fila-relacion-nombre button, a.fila-relacion-nombre")!
            .ClickAsync(new());

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(
            new WorkspaceFrame(EntidadWorkspace.Documento, seguro.Id, "Seguro", "informacion"));
    }

    [Fact]
    public void Un_vehiculo_recien_creado_pinta_el_anillo_sin_color_sin_banda_y_la_lista_vacia_con_subir()
    {
        var (id, mediador) = RecienCreado();
        Registrar(mediador);

        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll(".pestanas-panel .estado-vacio").Should().NotBeEmpty());

        var anillo = cut.Find("[data-pieza='anillo']");
        anillo.ClassList.Should().Contain("anillo-cumplimiento-solo-pista");
        anillo.QuerySelectorAll(".anillo-cumplimiento-progreso").Should().BeEmpty("sin documentos solo se dibuja la pista");
        SinEspaciosDeMas(anillo.QuerySelector(".anillo-cumplimiento-texto")!.TextContent).Should().Be("— 0 de 0");
        anillo.GetAttribute("aria-label").Should().Be("Sin documentos registrados");

        cut.FindAll("[data-pieza='banda']").Should().BeEmpty();
        var vacio = cut.Find(".pestanas-panel .estado-vacio");
        vacio.TextContent.Should().Contain("Sin documentos")
            .And.Contain("Todavía no hay documentos registrados para este vehículo.");
        Textos(vacio.QuerySelectorAll("button")).Should().Equal(["Subir documento"]);
        SinEspaciosDeMas(cut.Find(".pestanas-boton-activa .pestanas-contador").TextContent).Should().Be("0 documentos");
    }

    [Fact]
    public void Si_la_lista_falla_se_dice_y_se_ofrece_reintentar_sin_contador_inventado()
    {
        var (id, mediador) = CamionGrua();
        mediador.FallaDocumentos = true;
        Registrar(mediador);

        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll(".pestanas-panel .estado-vacio").Should().NotBeEmpty());

        var error = cut.Find(".pestanas-panel .estado-vacio");
        error.TextContent.Should().Contain("No pudimos cargar los documentos");
        Textos(error.QuerySelectorAll("button")).Should().Equal(["Reintentar"]);
        cut.Find(".pestanas-boton-activa").QuerySelectorAll(".pestanas-contador").Should().BeEmpty(
            "un fallo no se disfraza de «0 documentos»");
    }

    // ── Pestañas y lateral ────────────────────────────────────────────────

    [Fact]
    public void La_pestana_viaja_en_la_URL_y_el_historial_se_monta_para_el_Vehiculo()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);

        var cut = Renderizar(id, "?pestana=historial");

        cut.FindAll(".pestanas-boton").Should().HaveCount(2);
        cut.Find(".pestanas-boton-activa").TextContent.Should().Contain("Historial");
        var historial = cut.FindComponent<Stub<PestanaHistorial>>().Instance.Parameters;
        historial.Get(x => x.EntidadTipo).Should().Be("Vehiculo");
        historial.Get(x => x.EntidadId).Should().Be(id);
    }

    [Fact]
    public void Una_pestana_desconocida_cae_a_Documentacion()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);

        var cut = Renderizar(id, "?pestana=centros");

        cut.Find(".pestanas-boton-activa").TextContent.Should().Contain("Documentación");
    }

    [Fact]
    public void El_lateral_reune_nombre_matricula_modelo_y_empleador_y_avisa_de_que_no_se_cambia()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);

        var cut = Renderizar(id);

        var lateral = cut.Find("[data-pieza='lateral']");
        lateral.TextContent.Should().Contain("Información")
            .And.Contain("Camión grúa").And.Contain("9012 GHI").And.Contain("Iveco Daily").And.Contain("Montajes Skynet S.L.")
            .And.Contain("El empleador no se puede cambiar.");
        lateral.TextContent.Should().NotContain("Alta", "la fecha de alta no viaja en el detalle: es un dato nuevo, fuera del primer incremento");
    }

    [Fact]
    public async Task Editar_abre_el_panel_del_Vehiculo_y_al_cerrarlo_la_ficha_se_vuelve_a_leer()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));
        var panel = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.Find("[data-pieza='lateral'] button.boton").ClickAsync(new());

        panel.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Vehiculo, id, "Camión grúa", "informacion"));
        mediador.Enviadas.OfType<ObtenerVehiculoPorIdQuery>().Should().ContainSingle("abrir el panel no relee la ficha");

        mediador.Detalles[id] = mediador.Detalles[id] with { Nombre = "Camión grúa 2" };
        await cut.InvokeAsync(panel.CerrarAsync);

        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().Be("Camión grúa 2"));
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().HaveCount(2, "anillo y lista salen de la misma relectura");
    }

    [Fact]
    public async Task Si_al_cerrar_el_panel_el_Vehiculo_ya_no_existe_la_ficha_pasa_a_su_estado_de_error()
    {
        Services.ConEnlaceProfundoOtraEmpresa();
        var (id, mediador) = CamionGrua();
        Registrar(mediador);
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));
        var panel = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.Find("[data-pieza='lateral'] button.boton").ClickAsync(new());

        mediador.Detalles.Remove(id);
        await cut.InvokeAsync(panel.CerrarAsync);

        cut.WaitForAssertion(() => cut.Find(".estado-vacio").TextContent.Should().Contain("No pudimos cargar el vehículo"));
        cut.FindAll(".cabecera-identidad, [data-pieza='lateral'] button.boton").Should().BeEmpty(
            "una ficha vieja con «Editar» abriría el panel de un Vehículo que ya no se puede ver");
    }

    // ── Carreras y ciclo de vida ──────────────────────────────────────────

    [Fact]
    public async Task La_respuesta_tardia_de_un_Vehiculo_no_pisa_la_ficha_del_siguiente()
    {
        var (lento, mediador) = CamionGrua();
        var (rapido, otro) = RecienCreado();
        mediador.Detalles[rapido] = otro.Detalles[rapido];
        mediador.Compuertas[lento] = new TaskCompletionSource();
        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"vehiculos/{lento}");
        var cut = Render<VehiculoDetalle>(p => p.Add(x => x.VehiculoId, lento));

        cut.Render(p => p.Add(x => x.VehiculoId, rapido));
        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().Be("Furgoneta nueva"));

        mediador.Compuertas[lento].SetResult();
        await cut.InvokeAsync(() => Task.CompletedTask);

        cut.Find(".cabecera-identidad h1").TextContent.Trim().Should().Be("Furgoneta nueva",
            "la respuesta del Vehículo anterior llegó después: se descarta");
        cut.FindAll("[data-pieza='banda']").Should().BeEmpty();
    }

    [Fact]
    public async Task Al_retirar_la_pagina_deja_de_escuchar_al_panel()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));
        var panel = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.Find("[data-pieza='lateral'] button.boton").ClickAsync(new());
        var enviadasAntes = mediador.Enviadas.Count;

        cut.Instance.Dispose();
        await panel.CerrarAsync();

        mediador.Enviadas.Should().HaveCount(enviadasAntes, "una página retirada no vuelve a leer nada cuando el panel se cierra");
    }

    [Fact]
    public async Task Tras_guardar_un_documento_nuevo_la_ficha_se_vuelve_a_leer()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);
        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));

        mediador.Detalles[id] = mediador.Detalles[id] with { DocumentosAlDia = new FraccionCumplimiento(3, 5) };
        mediador.Documentos.Add(Documento("Tarjeta de transporte", EstadoDocumento.Vigente, Hoy.AddDays(300)));
        await cut.InvokeAsync(() => cut.FindComponent<DrawerGestionDocumento>().Instance.OnGuardado.InvokeAsync());

        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(5));
        SinEspaciosDeMas(cut.Find("[data-pieza='anillo'] .anillo-cumplimiento-texto").TextContent).Should().Be("60% 3 de 5");
    }

    [Fact]
    public void Si_el_vencido_no_cabe_en_la_lista_su_encabezado_lo_dice_sin_nombrarlo()
    {
        var (id, mediador) = CamionGrua();
        mediador.Documentos[0] = Documento("ITV", EstadoDocumento.Vigente, Hoy.AddDays(200));
        mediador.TotalDocumentos = 73;
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => cut.Find(".lista-relaciones-encabezado [data-vencidos-fuera]").TextContent.Should()
            .Be("Hay documentos vencidos que no caben en esta lista."));
        cut.FindAll("[data-pieza='banda']").Should().BeEmpty();
    }

    // ── Permisos ──────────────────────────────────────────────────────────

    [Fact]
    public void Con_escritura_hay_subir_documento_mas_acciones_y_editar()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-identidad");
        Textos(cabecera.QuerySelectorAll("button.boton-primario")).Should().Equal(["Subir documento"]);
        cabecera.QuerySelector(".menu-acciones button")!.GetAttribute("aria-label").Should().Be("Más acciones");
        Textos(cut.FindAll("[data-pieza='lateral'] button.boton")).Should().Equal(["Editar →", "Editar →"], "Información y Nota interna");
        cut.FindComponents<DrawerGestionDocumento>().Should().ContainSingle();
    }

    [Fact]
    public void En_Consulta_desaparece_la_escritura()
    {
        var (id, mediador) = CamionGrua();
        Registrar(mediador, Roles.Consulta);

        var cut = Renderizar(id);
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza='fila']").Count.Should().Be(4));

        var cabecera = cut.Find(".cabecera-identidad");
        cabecera.QuerySelectorAll("button.boton, .menu-acciones").Should().BeEmpty("subir y editar son escritura");
        cabecera.QuerySelector(".vehiculo360-placa button").Should().NotBeNull("copiar la matrícula no es escritura");
        cut.FindAll("[data-pieza='lateral'] button.boton").Should().BeEmpty();
        cut.FindComponents<DrawerGestionDocumento>().Should().BeEmpty();
    }

    // ── Error ─────────────────────────────────────────────────────────────

    [Fact]
    public void Fuera_de_alcance_o_inexistente_se_ven_igual_y_no_se_piden_sus_documentos()
    {
        Services.ConEnlaceProfundoOtraEmpresa();
        var mediador = Registrar(new MediatorFalso());

        var cut = Renderizar(Guid.NewGuid());

        cut.Find(".estado-vacio").TextContent.Should().Contain("No pudimos cargar el vehículo");
        cut.FindAll(".cabecera-identidad, [data-pieza='banda'], .pestanas").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerDocumentosQuery>().Should().BeEmpty(
            "sin detalle no hay ficha: pedir los documentos sería preguntar por un vehículo que no se puede ver");
    }

    // ── Nota interna ──────────────────────────────────────────────────────

    private const string NotaGuardada = "Aparca en la nave 2. Las llaves las tiene Leire.";

    private static (Guid Id, MediatorFalso Mediador) CamionGruaConNota(string? notas = NotaGuardada, bool notaInternaVisible = true)
    {
        var (id, mediador) = CamionGrua();
        mediador.Detalles[id] = mediador.Detalles[id] with { Notas = notas, NotaInternaVisible = notaInternaVisible };
        return (id, mediador);
    }

    private static IElement? TarjetaNota(IRenderedComponent<VehiculoDetalle> cut) =>
        cut.FindAll("[data-pieza=nota-interna]").SingleOrDefault();

    /// <summary>El editor es el único diálogo abierto con ese título: el drawer de documentos está cerrado.</summary>
    private static IElement EditorNota(IRenderedComponent<VehiculoDetalle> cut) =>
        cut.FindAll("[role=dialog]").Single(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private static bool EditorNotaAbierto(IRenderedComponent<VehiculoDetalle> cut) =>
        cut.FindAll("[role=dialog]").Any(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private static IRenderedComponent<VehiculoDetalle> AbrirEditorNota(IRenderedComponent<VehiculoDetalle> cut)
    {
        TarjetaNota(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeTrue());
        return cut;
    }

    [Fact]
    public void El_equipo_ve_la_nota_interna_al_final_del_lateral_con_su_pie()
    {
        var (id, mediador) = CamionGruaConNota();
        Registrar(mediador);

        var cut = Renderizar(id);

        var tarjeta = TarjetaNota(cut);
        tarjeta.Should().NotBeNull();
        tarjeta!.QuerySelector(".tarjeta-titulo")!.TextContent.Trim().Should().Be("Nota interna");
        tarjeta.TextContent.Should().Contain(NotaGuardada).And.Contain("No la ve el cliente.");
        tarjeta.TextContent.Should().NotContain("Sin nota interna.");
        cut.Find(".cuerpo-con-lateral-lateral").LastElementChild!.GetAttribute("data-pieza")
            .Should().Be("nota-interna", "la nota cierra el lateral, tras «Información»");
    }

    [Fact]
    public void Sin_nota_el_equipo_ve_la_tarjeta_con_su_vacio()
    {
        var (id, mediador) = CamionGruaConNota(notas: null);
        Registrar(mediador);

        var cut = Renderizar(id);

        TarjetaNota(cut)!.TextContent.Should().Contain("Sin nota interna.").And.Contain("No la ve el cliente.");
    }

    /// <summary>
    /// Si el servidor responde <c>NotaInternaVisible = false</c>, la página no pinta la tarjeta, ni siquiera vacía:
    /// «Sin nota interna.» ya diría que existe una nota del equipo.
    /// </summary>
    [Fact]
    public void Si_el_servidor_dice_que_no_es_visible_la_pagina_no_pinta_la_tarjeta_ni_vacia()
    {
        var (id, mediador) = CamionGruaConNota(notas: null, notaInternaVisible: false);
        Registrar(mediador);

        var cut = Renderizar(id);

        TarjetaNota(cut).Should().BeNull();
        cut.Markup.Should().NotContain("Nota interna").And.NotContain("No la ve el cliente.");
    }

    [Fact]
    public void Consulta_lee_la_nota_pero_no_se_le_ofrece_editarla()
    {
        var (id, mediador) = CamionGruaConNota();
        Registrar(mediador, Roles.Consulta);

        var cut = Renderizar(id);

        var tarjeta = TarjetaNota(cut);
        tarjeta!.TextContent.Should().Contain(NotaGuardada);
        tarjeta.QuerySelectorAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Editar_abre_el_editor_con_la_nota_actual_y_guardar_manda_solo_la_nota_con_la_version_leida()
    {
        var (id, mediador) = CamionGruaConNota();
        var versionLeida = mediador.Detalles[id].Version;
        var versionNueva = Guid.NewGuid();
        mediador.DetalleTrasGuardarNota = mediador.Detalles[id] with { Notas = "Ahora aparca fuera.", Version = versionNueva };
        Registrar(mediador);
        var cut = AbrirEditorNota(Renderizar(id));

        var campo = EditorNota(cut).QuerySelector("textarea")!;
        campo.TextContent.Should().Be(NotaGuardada);
        campo.Input("Ahora aparca fuera.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeFalse());
        mediador.Enviadas.OfType<GuardarNotaInternaVehiculoCommand>().Should().ContainSingle()
            .Which.Should().Be(new GuardarNotaInternaVehiculoCommand(id, "Ahora aparca fuera.", versionLeida));
        // La ficha se relee: la tarjeta enseña lo guardado y la siguiente orden lleva la versión nueva.
        TarjetaNota(cut)!.TextContent.Should().Contain("Ahora aparca fuera.").And.NotContain(NotaGuardada);
        AbrirEditorNota(cut);
        EditorNota(cut).QuerySelector("textarea")!.Input("Otra vez.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<GuardarNotaInternaVehiculoCommand>().Should().HaveCount(2));
        mediador.Enviadas.OfType<GuardarNotaInternaVehiculoCommand>().Last().Version.Should().Be(versionNueva);
    }

    [Fact]
    public void Si_el_servidor_rechaza_la_nota_el_editor_sigue_abierto_con_el_motivo_y_lo_escrito()
    {
        var rechazo = Error.Crear("Vehiculo.NotaRechazada", "La nota no se puede guardar ahora.");
        var (id, mediador) = CamionGruaConNota();
        mediador.ResultadoNota = Result.Fallo(rechazo);
        Registrar(mediador);
        var cut = AbrirEditorNota(Renderizar(id));

        EditorNota(cut).QuerySelector("textarea")!.Input("Lo mío.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNota(cut).QuerySelector(".drawer-aviso")!.TextContent.Should().Contain(rechazo.Mensaje));
        mediador.Enviadas.OfType<GuardarNotaInternaVehiculoCommand>().Should().ContainSingle();
        TarjetaNota(cut)!.TextContent.Should().Contain(NotaGuardada, "lo rechazado no se da por guardado");
    }

    /// <summary>
    /// Tras un conflicto el editor se cierra, la cabecera se relee (la tarjeta enseña la nota de la otra persona) y el
    /// texto propio queda como borrador: el usuario lo ve sobre la nota nueva antes de reaplicarlo.
    /// </summary>
    [Fact]
    public void Tras_un_conflicto_el_editor_se_cierra_y_la_nota_de_la_otra_persona_se_ve_antes_de_reaplicar()
    {
        var (id, mediador) = CamionGruaConNota();
        mediador.ResultadoNota = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona modificó este vehículo mientras lo editabas."));
        Registrar(mediador);
        var cut = AbrirEditorNota(Renderizar(id));
        // Lo que otra persona guardó entre medias: es lo que la ficha encontrará al releer.
        mediador.Detalles[id] = mediador.Detalles[id] with { Notas = "La de otra persona.", Version = Guid.NewGuid() };

        EditorNota(cut).QuerySelector("textarea")!.Input("Lo mío.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => TarjetaNota(cut)!.TextContent.Should().Contain("La de otra persona."));
        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeFalse());
        // Un único envío: la ficha no reenvía con la versión nueva sin que el usuario vea la nota de esa persona.
        mediador.Enviadas.OfType<GuardarNotaInternaVehiculoCommand>().Should().HaveCount(1);

        // El borrador propio sobrevive al conflicto: el usuario lo reaplica sobre la nota nueva, no se pierde.
        AbrirEditorNota(cut);
        EditorNota(cut).QuerySelector("textarea")!.TextContent.Should().Be("Lo mío.");
    }

    /// <summary>La nota demasiado larga la rechaza el validador del servidor (ValidationBehavior lanza): el motivo va al campo.</summary>
    [Fact]
    public void Si_el_validador_rechaza_la_nota_el_motivo_se_ve_en_el_campo_y_el_editor_sigue_abierto()
    {
        const string motivo = "La nota interna no puede superar 2000 caracteres.";
        var (id, mediador) = CamionGruaConNota();
        mediador.ExcepcionAlGuardarNota = new FluentValidation.ValidationException(
            [new FluentValidation.Results.ValidationFailure(nameof(GuardarNotaInternaVehiculoCommand.Notas), motivo)]);
        Registrar(mediador);
        var cut = AbrirEditorNota(Renderizar(id));

        EditorNota(cut).QuerySelector("textarea")!.Input("Demasiado larga.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNota(cut).QuerySelector(".campo-mensaje-error")!.TextContent.Should().Be(motivo));
        EditorNota(cut).QuerySelectorAll(".drawer-aviso").Should().BeEmpty("el error es del campo, no del formulario");
    }

    [Fact]
    public async Task Cancelar_con_la_nota_cambiada_pregunta_antes_de_descartar_y_sin_cambios_cierra()
    {
        var (id, mediador) = CamionGruaConNota();
        Registrar(mediador);
        var cut = AbrirEditorNota(Renderizar(id));

        await EditorNota(cut).QuerySelector(".drawer-pie .boton-secundario")!.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeFalse("sin cambios no hay nada que preguntar"));

        AbrirEditorNota(cut);
        EditorNota(cut).QuerySelector("textarea")!.Input("A medio escribir.");
        await EditorNota(cut).QuerySelector(".drawer-pie .boton-secundario")!.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll("button").Any(b => b.TextContent.Trim() == "Descartar cambios").Should().BeTrue());
        EditorNotaAbierto(cut).Should().BeTrue();
        mediador.Enviadas.Should().NotContain(p => p is GuardarNotaInternaVehiculoCommand);
    }
}
