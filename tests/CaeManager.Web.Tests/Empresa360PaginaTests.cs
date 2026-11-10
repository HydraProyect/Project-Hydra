using CaeManager.Application.Empresas.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.PaqueteDocumentalEmpresa;
using CaeManager.Web.Components.Layout;
using AngleSharp.Dom;
using Bunit.TestDoubles;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Queries.ObtenerAcreditacionesPorProveedor;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Empresas.Commands.GuardarNotaInternaEmpresa;
using CaeManager.Application.Empresas.Commands.EditarFichaEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Components;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Empresa 360, la página (<see cref="EmpresaDetalle"/>, <c>/empresas/{id}</c>)
/// contra su mockup «Empresa 360 página TALVEG». Prueban efectos: qué se ve,
/// qué consultas salen y con qué parámetros, qué queda en la URL y qué panel
/// se abre. bUnit no evalúa CSS, así que ninguno afirma un estilo.
///
/// <para>
/// El mediador falso responde a <see cref="ObtenerTrabajadoresQuery"/> con la
/// MISMA semántica que el handler real —filtro de un solo estado, orden por
/// peor estado (Vencido, Urgente, Próximo, Vigente, Sin caducidad) y
/// paginación—: la página compone sus chips como tramos de ese orden, y un
/// falso que no ordenara daría verde con una página que mezcla estados.
/// </para>
/// </summary>
public partial class Empresa360PaginaTests : BunitContext
{
    public Empresa360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/cajas-ficha.js").Setup<double[]>("medirAlturas", _ => true).SetResult([200, 100]);
        // Las pestañas que no son de esta página se sustituyen: tienen sus
        // propios tests, y aquí solo importa que se monten con el Id correcto.
        ComponentFactories.AddStub<PestanaDocumentacion>();
        ComponentFactories.AddStub<PestanaAgendaContactos>();
        ComponentFactories.AddStub<PestanaSelloEmpresa>();
        ComponentFactories.AddStub<PestanaHistorial>();
        ComponentFactories.AddStub<PestanaVigenciaEmpresa>();
        ComponentFactories.AddStub<PestanaPlataformasEmpresa>();
        ComponentFactories.AddStub<PestanaReclamarEmpresa>();
    }

    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, EmpresaDetalleDto> Detalles { get; } = [];
        public bool FallarAgenda { get; set; }
        public Dictionary<Guid, int?> Cumplimiento { get; } = [];
        public Dictionary<Guid, List<TrabajadorListaDto>> Trabajadores { get; } = [];
        public Dictionary<Guid, List<ClienteDeEmpresaDto>> Clientes { get; } = [];
        public Dictionary<Guid, CredencialAccesoEmpresaDto> Credenciales { get; } = [];
        public Dictionary<Guid, SelloEmpresaDto> Sellos { get; } = [];
        public Dictionary<Guid, List<ContactoAgendaDto>> Agenda { get; } = [];
        public Dictionary<Guid, CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.CatalogoTrabajadoresEmpresaDto> Catalogos { get; } = [];

        /// <summary>La fila de cada Empresa en el listado /empresas (lo que devuelve ObtenerEmpresasQuery con EmpresaId). Sin entrada: el listado no la devuelve.</summary>
        public Dictionary<Guid, EmpresaListaDto> FilasDelListado { get; } = [];
        public bool FallarListado { get; set; }

        /// <summary>Lo que lee la pestaña Plataformas (y su contador) por Empresa, tal como lo devuelve ObtenerAcreditacionesPorProveedorQuery.</summary>
        public Dictionary<Guid, IReadOnlyList<ProveedorAcreditacionesDto>> Plataformas { get; } = [];
        public bool FallarPlataformas { get; set; }

        public List<object> Enviadas { get; } = [];

        /// <summary>Lo que responde ObtenerResumenPaqueteEmpresaQuery («Preparar paquete»).</summary>
        public Func<ObtenerResumenPaqueteEmpresaQuery, Result<ResumenPaqueteEmpresaDto>> ResumenPaquete { get; set; } =
            _ => throw new NotSupportedException("El resumen del paquete no está previsto en este test.");

        /// <summary>Como AutorizacionSecretosDeTenantBehavior con un rol que lee secretos y sin 2FA (P1-I1).</summary>
        public bool SinDobleFactor { get; set; }
        public Func<object, Task?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (Retener?.Invoke(request) is { } espera) await espera;
            if (SinDobleFactor && request is IConsultaDeSecretosDeTenant or IConsultaDeDatosDeCredencial)
                throw new SegundoFactorRequeridoParaCredencialesException();
            return (TResponse)Responder(request)!;
        }

        private object? Responder(object request) => request switch
        {
            ObtenerEmpresaPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerSelloEmpresaQuery q => Sellos.GetValueOrDefault(q.EmpresaId),
            ObtenerAgendaContactosQuery when FallarAgenda => throw new InvalidOperationException("Fallo simulado de la agenda."),
            ObtenerAgendaContactosQuery q => (IReadOnlyList<ContactoAgendaDto>)(Agenda.GetValueOrDefault(q.PropietarioId) ?? []),
            ObtenerCumplimientoEmpresaQuery q => Cumplimiento.GetValueOrDefault(q.EmpresaId),
            ObtenerTrabajadoresQuery q => ResponderTrabajadores(q),
            ObtenerEmpresasQuery when FallarListado => throw new InvalidOperationException("Fallo simulado del listado."),
            ObtenerEmpresasQuery q => new ResultadoPaginado<EmpresaListaDto>(
                q.EmpresaId is { } id && FilasDelListado.TryGetValue(id, out var fila) ? [fila] : [], 0, 1, 20),
            ObtenerAcreditacionesPorProveedorQuery when FallarPlataformas => throw new InvalidOperationException("Fallo simulado de las plataformas."),
            ObtenerAcreditacionesPorProveedorQuery q => q.SoloDocumentosDeEmpresaId is { } id && Plataformas.TryGetValue(id, out var proveedores)
                ? proveedores
                : (IReadOnlyList<ProveedorAcreditacionesDto>)[],
            ObtenerClientesDeEmpresaQuery q => (IReadOnlyList<ClienteDeEmpresaDto>)(Clientes.GetValueOrDefault(q.EmpresaId) ?? []),
            ObtenerCredencialAccesoEmpresaSinContrasenaQuery q => Credenciales.TryGetValue(q.EmpresaId, out var c)
                ? new CredencialAccesoEmpresaSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas)
                : null,
            ObtenerCredencialAccesoEmpresaQuery q => Credenciales.GetValueOrDefault(q.EmpresaId),
            CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.ObtenerCatalogoTrabajadoresEmpresaQuery q => Catalogos.GetValueOrDefault(q.EmpresaId) ?? CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.CatalogoTrabajadoresEmpresaDto.Vacio,
            ObtenerResumenPaqueteEmpresaQuery q => ResumenPaquete(q),
            GuardarNotaInternaEmpresaCommand c => GuardarNota(c),
            EditarFichaEmpresaCommand c => GuardarFicha(c),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        /// <summary>Lo que responde la orden de guardar la nota; en éxito, la ficha releída pasa a ser esta.</summary>
        public Result ResultadoNota { get; set; } = Result.Exito();
        public EmpresaDetalleDto? DetalleTrasGuardarNota { get; set; }

        private Result GuardarNota(GuardarNotaInternaEmpresaCommand c)
        {
            if (ResultadoNota.EsExitoso && DetalleTrasGuardarNota is not null)
                Detalles[c.Id] = DetalleTrasGuardarNota;
            return ResultadoNota;
        }

        public Result ResultadoFicha { get; set; } = Result.Exito();
        public EmpresaDetalleDto? DetalleTrasGuardarFicha { get; set; }
        private Result GuardarFicha(EditarFichaEmpresaCommand c)
        {
            if (ResultadoFicha.EsExitoso && DetalleTrasGuardarFicha is not null)
                Detalles[c.Id] = DetalleTrasGuardarFicha;
            return ResultadoFicha;
        }

        /// <summary>Misma semántica que ObtenerTrabajadoresQueryHandler (ver la doc de la clase).</summary>
        private ResultadoPaginado<TrabajadorListaDto> ResponderTrabajadores(ObtenerTrabajadoresQuery q)
        {
            IEnumerable<TrabajadorListaDto> filas = q.EmpresaId is { } id ? Trabajadores.GetValueOrDefault(id) ?? [] : [];
            if (!string.IsNullOrWhiteSpace(q.Busqueda))
                filas = filas.Where(t => $"{t.Nombre} {t.Apellidos}".Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase));
            if (q.DelegacionEmpresaId is { } delegacionId)
                filas = filas.Where(t => t.DelegacionEmpresaId == delegacionId);
            if (q.OficioEmpresaId is { } oficioId)
                filas = filas.Where(t => t.OficioEmpresaId == oficioId);
            if (!string.IsNullOrWhiteSpace(q.EstadoDocumental))
                filas = filas.Where(t => EstadoDocumentalFiltro.Coincide(t.EstadoDocumental, q.EstadoDocumental));
            if (q.OrdenarPor == nameof(TrabajadorListaDto.EstadoDocumental))
                filas = filas.OrderBy(t => Rango(t.EstadoDocumental)).ThenBy(t => t.Apellidos).ThenBy(t => t.Nombre).ThenBy(t => t.Id);
            else if (q.OrdenarPor == nameof(TrabajadorListaDto.DelegacionEmpresaNombre))
                filas = filas.OrderBy(t => t.DelegacionEmpresaNombre is null).ThenBy(t => t.DelegacionEmpresaNombre, StringComparer.Ordinal).ThenBy(t => t.Apellidos).ThenBy(t => t.Id);
            else if (q.OrdenarPor == nameof(TrabajadorListaDto.OficioNombre))
                filas = filas.OrderBy(t => t.OficioNombre is null).ThenBy(t => t.OficioNombre, StringComparer.Ordinal).ThenBy(t => t.Apellidos).ThenBy(t => t.Id);

            var lista = filas.ToList();
            return new ResultadoPaginado<TrabajadorListaDto>(
                lista.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), lista.Count, q.Pagina, q.TamanoPagina);
        }

        private static int Rango(EstadoDocumento? estado) => estado switch
        {
            // El handler ordena con EstadoDocumentalFiltro.ClaveOrdenConFaltantes: lo Pendiente va justo detrás de lo vencido.
            EstadoDocumento.Vencido => 0,
            EstadoDocumento.Faltante => 1,
            EstadoDocumento.Urgente => 2,
            EstadoDocumento.Proximo => 3,
            EstadoDocumento.Vigente => 4,
            _ => 5
        };

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

    private static readonly Guid EmpresaId = Guid.NewGuid();

    private MediatorFalso Registrar(MediatorFalso mediador, string rol = Roles.GestorCae)
    {
        this.ConRolDeEscritura(rol);
        this.ConServiciosDelFormularioDeDocumento();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        // La credencial se carga en segundo plano solo si el render es
        // interactivo (como Centro 360): el test declara el modo de producción.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<EmpresaDetalle> Renderizar(string consulta = "")
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"empresas/{EmpresaId}{consulta}");
        var cut = Render<EmpresaDetalle>(p => p.Add(x => x.EmpresaId, EmpresaId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());
        return cut;
    }

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();
    private ContextWorkspaceService Workspace => Services.GetRequiredService<ContextWorkspaceService>();

    private static int _secuencia;

    private static TrabajadorListaDto Trabajador(EstadoDocumento? estado, string? apellidos = null) =>
        new(Guid.NewGuid(), "Ana", apellidos ?? $"Trabajador {Interlocked.Increment(ref _secuencia):D3}",
            $"{_secuencia:D8}Z", "Ibertec GmbH", estado);

    /// <summary>
    /// 25 trabajadores: 3 vencidos, 2 urgentes, 1 próximo, 12 vigentes y 7 sin
    /// caducidad, en desorden: la página tiene que llegar a la partición y al
    /// orden por sus consultas, no por cómo vienen.
    /// </summary>
    private static List<TrabajadorListaDto> Plantilla() =>
    [
        .. Enumerable.Range(0, 12).Select(_ => Trabajador(EstadoDocumento.Vigente)),
        Trabajador(EstadoDocumento.Proximo, "Próximo Uno"),
        .. Enumerable.Range(0, 7).Select(_ => Trabajador(EstadoDocumento.SinCaducidad)),
        Trabajador(EstadoDocumento.Vencido, "Vencido Uno"),
        Trabajador(EstadoDocumento.Urgente, "Urgente Uno"),
        Trabajador(EstadoDocumento.Vencido, "Vencido Dos"),
        Trabajador(EstadoDocumento.Urgente, "Urgente Dos"),
        Trabajador(EstadoDocumento.Vencido, "Vencido Tres"),
    ];

    private static MediatorFalso Empresa(List<TrabajadorListaDto>? trabajadores = null, int? cumplimiento = 72)
    {
        var mediador = new MediatorFalso();
        mediador.Detalles[EmpresaId] = new EmpresaDetalleDto(
            EmpresaId, "Ibertec GmbH", "B-48.220.917", new DateTime(2025, 3, 14, 10, 0, 0, DateTimeKind.Utc),
            [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid(), "4322", "Metal de Bizkaia", true) with
        { FichaVisible = true };
        mediador.Sellos[EmpresaId] = new SelloEmpresaDto("/sello.png", DateTime.UtcNow);
        mediador.Cumplimiento[EmpresaId] = cumplimiento;
        mediador.Trabajadores[EmpresaId] = trabajadores ?? Plantilla();
        return mediador;
    }

    private static List<string> Chips(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".empresa360-chip").Select(b => b.TextContent.Trim()).ToList();

    private static string ChipPulsado(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".empresa360-chip").Single(b => b.GetAttribute("aria-pressed") == "true").TextContent.Trim();

    private static List<string> EstadosDeLasFilas(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".fila-relacion .badge").Select(b => b.TextContent.Trim()).ToList();

    /// <summary>
    /// El estado de CÓDIGO de cada fila, por el Id de su enlace. Desde el vocabulario único (2026-10-08) Urgente
    /// y Próximo se rotulan igual («Por vencer») y Vigente y Sin caducidad también («Sin incidencias»): el orden
    /// dentro de un mismo rótulo —lo urgente delante de lo próximo— ya no se lee en la pastilla, solo en el dato.
    /// </summary>
    private static List<EstadoDocumento?> EstadosDeCodigoDeLasFilas(IRenderedComponent<EmpresaDetalle> cut, MediatorFalso mediador) =>
        cut.FindAll(".fila-relacion-nombre")
            .Select(a => mediador.Trabajadores[EmpresaId].Single(t => a.GetAttribute("href") == $"/trabajadores/{t.Id}").EstadoDocumental)
            .ToList();

    private static IElement Pestana(IRenderedComponent<EmpresaDetalle> cut, string texto) =>
        cut.FindAll("[role=tab]").Single(b => b.TextContent.Trim().StartsWith(texto, StringComparison.Ordinal));

    // ── Carga y cabecera ──────────────────────────────────────────────────

    /// <summary>
    /// M-06: un fallo al leer la agenda se leía como «sin contacto principal». El usuario recibe un
    /// aviso de error y la cabecera sigue pintándose.
    /// </summary>
    [Fact]
    public void Si_la_agenda_de_contactos_falla_avisa_al_usuario_y_la_cabecera_sigue()
    {
        var mediador = Empresa();
        mediador.FallarAgenda = true;
        Registrar(mediador);

        var cut = Renderizar();

        cut.Find("[data-pieza=cabecera-identidad]").Should().NotBeNull();
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Tono == TonoToast.Error);
    }

    [Fact]
    public void La_cabecera_pinta_identidad_recuentos_y_anillo_con_las_consultas_del_panel()
    {
        var mediador = Empresa();
        mediador.Clientes[EmpresaId] =
        [
            new ClienteDeEmpresaDto(Guid.NewGuid(), "Refrielectric S.A.", "A-12345678"),
            new ClienteDeEmpresaDto(Guid.NewGuid(), "Aislamientos Nervión S.L.", "B-87654321")
        ];
        Registrar(mediador);

        var cut = Renderizar();

        // La identidad va en la tarjeta de cabecera del patrón 360, no en la cabecera suelta de pantalla.
        var cabecera = cut.Find("[data-pieza=cabecera-identidad]");
        cut.FindAll(".cabecera-pagina").Should().BeEmpty();
        cabecera.QuerySelector(".cabecera-identidad-kicker")!.TextContent.Trim().Should().Be("Empresa");
        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Ibertec GmbH");
        cabecera.QuerySelector("h1").Should().NotBeNull();
        var entradilla = cabecera.QuerySelector(".cabecera-identidad-datos")!.TextContent;
        // «CIF» y el valor van en nodos separados (maqueta «Empresa 360»: .cif-copiable con icono de copiar), sin el rótulo «Copiar».
        entradilla.Should().Contain("CIF").And.Contain("B-48.220.917").And.Contain("2 Clientes").And.Contain("25 trabajadores");
        var cif = cabecera.QuerySelector(".cabecera-identidad-datos .boton-copiar")!;
        cif.TextContent.Should().Contain("B-48.220.917").And.NotContain("Copiar", "el dato es el botón y lleva el icono de copiar, no el texto «Copiar»");
        cif.ClassList.Should().Contain("boton-copiar-con-icono");
        cif.GetAttribute("aria-label").Should().Be("Copiar el CIF B-48.220.917");
        cut.Find("[role=img]").GetAttribute("aria-label").Should().StartWith("72% de los documentos exigidos");
        // El anillo va a la izquierda de la identidad, no entre las acciones (mockup).
        cabecera.QuerySelector(".cabecera-identidad-anillo [role=img]").Should().NotBeNull();
        cabecera.QuerySelector(".cabecera-identidad-acciones")!.QuerySelector("[role=img]").Should().BeNull();
        // «Detectar altas y bajas» y el menú siguen en la cabecera.
        cabecera.QuerySelector(".cabecera-identidad-acciones")!.TextContent.Should().Contain("Detectar altas y bajas");
        // Maqueta «Empresa 360 página»: los dos enlaces de la cabecera son botones secundarios de 13 px, sin subrayado ni borde propio.
        cabecera.QuerySelector(".cabecera-identidad-acciones")!.QuerySelectorAll("a.boton")
            .Where(a => a.TextContent.Contains("Detectar altas y bajas") || a.TextContent.Contains("Reclamar para acceso"))
            .Should().NotBeEmpty().And.OnlyContain(a => a.ClassList.Contains("empresa360-accion-cabecera"));
        cabecera.QuerySelector(".cabecera-identidad-acciones .menu-acciones-disparador")!.GetAttribute("title").Should().Be("Más acciones");

        mediador.Enviadas.Should().Contain(new ObtenerEmpresaPorIdQuery(EmpresaId));
        mediador.Enviadas.Should().Contain(new ObtenerCumplimientoEmpresaQuery(EmpresaId));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().OnlyContain(q => q.EmpresaId == EmpresaId,
            "los trabajadores son los de ESTA Empresa, con el alcance que aplica el handler");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().OnlyContain(q => q.TamanoPagina <= 20,
            "nunca se trae la plantilla entera para contar");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void La_cabecera_ofrece_Subir_documento_como_accion_principal_solo_con_escritura(string rol, bool escribe)
    {
        Registrar(Empresa(), rol);

        var acciones = Renderizar().Find(".cabecera-identidad-acciones");

        var subir = acciones.QuerySelectorAll("button").SingleOrDefault(b => b.TextContent.Trim() == "Subir documento");
        (subir is not null).Should().Be(escribe);
        if (escribe)
            subir!.ClassList.Should().Contain("boton-primario");
        acciones.QuerySelectorAll("a").Single(x => x.TextContent.Contains("Detectar altas y bajas"))
            .ClassList.Should().NotContain("boton-primario", "la acción principal es subir el documento");
    }
    // ── Chip de estado documental y contador de Plataformas (maqueta «Empresa 360») ──
    // Los dos salen de la carga en segundo plano de la ficha (después del acceso a la carpeta): el esqueleto no espera a esas dos consultas.

    private static EmpresaListaDto FilaDelListado(EstadoDocumento? estado, bool vencidoSinBloqueo = false) =>
        new(EmpresaId, "Ibertec GmbH", "B-48.220.917", DateTime.UtcNow, estado) { VencidoSinBloqueo = vencidoSinBloqueo };

    private static IElement? ChipEstado(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.Find(".cabecera-identidad-datos").QuerySelector(".badge");

    /// <summary>
    /// Espera al último paso del segundo plano (el contador de Plataformas) y a que el repintado que lo sigue haya corrido, para que
    /// las pruebas de AUSENCIA no pasen en vacío por mirar antes de tiempo.
    /// </summary>
    private static async Task EsperarSegundoPlanoAsync(IRenderedComponent<EmpresaDetalle> cut, MediatorFalso mediador)
    {
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().NotBeEmpty());
        await cut.InvokeAsync(() => { });
    }

    [Fact]
    public void El_chip_de_estado_documental_sale_de_la_misma_consulta_de_fila_que_el_listado()
    {
        var mediador = Empresa();
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(EstadoDocumento.Vencido);
        Registrar(mediador);

        var cut = Renderizar();

        cut.WaitForAssertion(() => ChipEstado(cut).Should().NotBeNull());
        var chip = ChipEstado(cut)!;
        chip.TextContent.Trim().Should().Be("Vencido");
        chip.ClassList.Should().Contain("badge-peligro").And.Contain("badge-pequeno");
        // La pregunta es la del refresco de una fila del listado: por Id y con los recuentos por estado, sin lo cual el handler
        // deja sin estado a quien no tiene documentos y la ficha diría «Sin documentos» donde la lista dice «Sin incidencias».
        var consulta = mediador.Enviadas.OfType<ObtenerEmpresasQuery>().Should().ContainSingle().Subject;
        consulta.EmpresaId.Should().Be(EmpresaId);
        consulta.ConRecuentosPorEstado.Should().BeTrue();
    }

    [Fact]
    public void Un_Vencido_que_ningun_Centro_marca_obligatorio_lleva_el_tono_de_aviso_como_en_la_lista()
    {
        var mediador = Empresa();
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(EstadoDocumento.Vencido, vencidoSinBloqueo: true);
        Registrar(mediador);

        var cut = Renderizar();

        cut.WaitForAssertion(() => ChipEstado(cut).Should().NotBeNull());
        var chip = ChipEstado(cut)!;
        chip.TextContent.Trim().Should().Be("Vencido");
        chip.ClassList.Should().Contain("badge-advertencia").And.NotContain("badge-peligro");
    }

    [Theory]
    [InlineData(EstadoDocumento.Faltante, "Pendiente")]
    [InlineData(EstadoDocumento.Urgente, "Por vencer")]
    [InlineData(EstadoDocumento.Proximo, "Por vencer")]
    public void El_chip_usa_el_rotulo_del_listado_para_cada_estado_que_hay_que_atender(EstadoDocumento estado, string rotulo)
    {
        var mediador = Empresa();
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(estado);
        Registrar(mediador);

        var cut = Renderizar();

        cut.WaitForAssertion(() => ChipEstado(cut).Should().NotBeNull());
        ChipEstado(cut)!.TextContent.Trim().Should().Be(rotulo);
    }

    [Theory]
    [InlineData(EstadoDocumento.Vigente)]
    [InlineData(EstadoDocumento.SinCaducidad)]
    public void Lo_correcto_se_dice_en_texto_sin_pastilla_como_en_la_lista(EstadoDocumento estado)
    {
        var mediador = Empresa();
        mediador.FilasDelListado[EmpresaId] = FilaDelListado(estado);
        Registrar(mediador);

        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("Sin incidencias"));
        ChipEstado(cut).Should().BeNull("el color queda para lo que hay que atender");
    }

    [Fact]
    public async Task Sin_fila_en_el_listado_no_hay_chip_y_la_cabecera_sigue()
    {
        var sinFila = Empresa();
        Registrar(sinFila);

        var cut = Renderizar();
        await EsperarSegundoPlanoAsync(cut, sinFila);

        sinFila.Enviadas.OfType<ObtenerEmpresasQuery>().Should().ContainSingle("la consulta salió y no devolvió fila");
        ChipEstado(cut).Should().BeNull("sin fila en el listado no se supone ningún estado");
        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().Contain("B-48.220.917");
    }

    [Fact]
    public async Task Si_la_consulta_del_listado_falla_no_hay_chip_ni_error_de_pagina()
    {
        var mediador = Empresa();
        mediador.FallarListado = true;
        Registrar(mediador);

        var cut = Renderizar();
        await EsperarSegundoPlanoAsync(cut, mediador);

        ChipEstado(cut).Should().BeNull();
        cut.Find("h1").TextContent.Trim().Should().Be("Ibertec GmbH");
    }

    private static List<ProveedorAcreditacionesDto> PlataformasConIncidencias(int pendientes, int validados = 1)
    {
        AcreditacionDrillDownDto Doc(string tipo, EstadoAcreditacion estado, Guid centro) =>
            new(Guid.NewGuid(), Guid.NewGuid(), "Ibertec GmbH", tipo, estado, null, EmpresaId: EmpresaId, CentroId: centro, CentroNombre: "Planta Zaragoza");
        var centro = Guid.NewGuid();
        return
        [
            new(Guid.NewGuid(), "Nalanda", "NALANDA",
            [
                new ClienteAcreditacionesDto(Guid.NewGuid(), "Refrielectric S.A.",
                [
                    .. Enumerable.Range(0, pendientes).Select(i => Doc($"Pendiente {i}", EstadoAcreditacion.PendienteDeSubir, centro)),
                    .. Enumerable.Range(0, validados).Select(i => Doc($"Validado {i}", EstadoAcreditacion.Aceptada, centro))
                ])
            ])
        ];
    }

    [Fact]
    public void El_contador_de_Plataformas_cuenta_las_incidencias_sin_abrir_la_pestana()
    {
        var mediador = Empresa();
        mediador.Plataformas[EmpresaId] = PlataformasConIncidencias(pendientes: 3, validados: 2);
        Registrar(mediador);

        var cut = Renderizar();

        cut.WaitForAssertion(() => Pestana(cut, "Plataformas").TextContent.Should().Contain("3", "las validadas no son incidencias"));
        Pestana(cut, "Trabajadores").GetAttribute("aria-selected").Should().Be("true", "la pestaña Plataformas sigue cerrada");
        // El universo es el de la pestaña: la misma petición que ella hace, acotada a ESTA Empresa.
        var consulta = mediador.Enviadas.OfType<ObtenerAcreditacionesPorProveedorQuery>().Should().ContainSingle().Subject;
        consulta.Should().Be(new ObtenerAcreditacionesPorProveedorQuery(IncluirAceptadas: true, IncluirSubidas: true, SoloDocumentosDeEmpresaId: EmpresaId),
            "es la misma petición que hace la pestaña (Empresa360VigenciaPlataformasTests)");
    }

    [Fact]
    public async Task Con_todo_validado_la_pestana_Plataformas_no_lleva_contador()
    {
        var mediador = Empresa();
        mediador.Plataformas[EmpresaId] = PlataformasConIncidencias(pendientes: 0, validados: 4);
        Registrar(mediador);

        var cut = Renderizar();
        await EsperarSegundoPlanoAsync(cut, mediador);

        Pestana(cut, "Plataformas").TextContent.Trim().Should().Be("Plataformas", "cero incidencias no se enseña como contador");
    }

    [Fact]
    public async Task Si_la_lectura_de_Plataformas_falla_la_pestana_no_lleva_contador_y_la_ficha_carga()
    {
        var mediador = Empresa();
        mediador.FallarPlataformas = true;
        Registrar(mediador);

        var cut = Renderizar();
        await EsperarSegundoPlanoAsync(cut, mediador);

        Pestana(cut, "Plataformas").TextContent.Trim().Should().Be("Plataformas");
        cut.Find("h1").TextContent.Trim().Should().Be("Ibertec GmbH");
    }

    // ---- «Preparar paquete» (maqueta «Mapa Dia a Dia TALVEG.dc.html» § C, enlazada desde la cabecera de Empresa 360)

    private static readonly Guid ClientePaqueteId = Guid.NewGuid();
    private static readonly Guid OtroClientePaqueteId = Guid.NewGuid();

    private static ResumenPaqueteEmpresaDto ResumenPaqueteDe(Guid? clienteId) => clienteId is null
        ? new ResumenPaqueteEmpresaDto("Montajes Ebro SL", Destinos, null, 40, 0, 0, [])
        : new ResumenPaqueteEmpresaDto("Montajes Ebro SL", Destinos, clienteId, 31, 2, 9,
        [
            new FaltaPaqueteEmpresaDto("Seguro de Responsabilidad Civil", null, null),
            new FaltaPaqueteEmpresaDto("Certificado de aptitud médica", "Paula Campos Lara", new DateOnly(2026, 8, 2)),
        ]);

    private static readonly IReadOnlyList<DestinoPaqueteEmpresaDto> Destinos =
        [new(ClientePaqueteId, "Refrielectric S.A."), new(OtroClientePaqueteId, "Grupo Arbeko")];

    private IRenderedComponent<EmpresaDetalle> AbrirPaquete(MediatorFalso mediador)
    {
        mediador.ResumenPaquete = q => Result.Exito(ResumenPaqueteDe(q.ClienteEmpresarialId));
        var cut = Renderizar();
        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();
        return cut;
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void La_cabecera_ofrece_Preparar_paquete_solo_a_un_rol_que_descarga(string rol, bool ofrece)
    {
        Registrar(Empresa(), rol);

        Renderizar().Find(".cabecera-identidad-acciones").TextContent.Contains("Preparar paquete").Should().Be(ofrece,
            "el zip sale de TALVEG con documentos sensibles: la misma lista blanca que DescargarPaqueteEmpresaQuery");
    }

    [Fact]
    public void Generar_ZIP_lleva_el_icono_de_descarga_decorativo_como_la_maqueta()
    {
        var cut = AbrirPaquete(Registrar(Empresa()));

        var generar = cut.Find("[role=dialog] .paquete-generar");
        generar.QuerySelectorAll("svg.icono-pequeno[aria-hidden=true]").Should().ContainSingle(
            "la maqueta (§ C) pinta un icono de descarga de 16 px (TamanoIcono.Pequeno) delante del texto");
        // Es el icono «descargar» y no cualquier SVG pequeño: el marcado tiene que ser el que pinta Icono con ese nombre.
        var descargar = Render<Icono>(p => p.Add(x => x.Nombre, "descargar").Add(x => x.Tamano, TamanoIcono.Pequeno));
        generar.QuerySelector("svg")!.MarkupMatches(descargar.Markup);
        generar.TextContent.Trim().Should().Be("Generar ZIP", "el icono es decorativo: el nombre accesible no cambia");
    }

    [Fact]
    public void Mientras_se_genera_Generar_ZIP_pinta_el_spinner_y_no_el_icono_de_descarga()
    {
        var importacion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, importacion.Task));
        var cut = AbrirPaquete(Registrar(Empresa()));

        cut.Find("[role=dialog] .paquete-generar").Click();

        var generando = cut.Find("[role=dialog] .paquete-generar");
        generando.QuerySelectorAll(".boton-spinner").Should().ContainSingle("Boton pinta su spinner mientras se genera");
        generando.QuerySelectorAll("svg").Should().BeEmpty("el spinner sustituye al icono: los dos a la vez no son la maqueta");

        importacion.SetResult(new ModuloDescargaQueAnota());
        cut.WaitForAssertion(() => cut.Find("[role=dialog] .paquete-generar").QuerySelectorAll("svg").Should().ContainSingle(
            "terminada la descarga vuelve el icono"));
    }

    [Fact]
    public void El_dialogo_cuelga_del_contenedor_al_que_el_CSS_del_componente_fija_los_600_px_de_la_maqueta()
    {
        var cut = AbrirPaquete(Registrar(Empresa()));

        cut.Find(".paquete-dialogo .modal-contenido-grande[role=dialog]").Should().NotBeNull(
            "la regla de ancho es `.paquete-dialogo ::deep .modal-contenido-grande`: sin el contenedor, o con otro AnchoModal, no casa con nada y el diálogo no mide 600 px");
    }

    // bUnit no aplica CSS: lo que sí se puede fijar es el texto de las hojas (revisión píxel a píxel de #2100, divergencias 1, 3 y 4).
    [Fact]
    public void Las_hojas_fijan_13_px_en_el_boton_de_la_cabecera_600_px_en_el_dialogo_y_la_familia_de_la_aplicacion()
    {
        var detalle = LeerHoja("Pages", "EmpresaDetalle.razor.css");
        detalle.Should().Contain(".contenedor-pagina ::deep .boton.empresa360-accion-cabecera",
            "el botón de «Preparar paquete» es un <button>: el selector no puede exigir `a.`");
        detalle.Should().NotContain("a.boton.empresa360-accion-cabecera");
        System.Text.RegularExpressions.Regex.IsMatch(detalle, @"\.contenedor-pagina ::deep \.boton\.empresa360-accion-cabecera\s*\{\s*font-size:\s*0\.8125rem;\s*\}")
            .Should().BeTrue("las cuatro acciones de la cabecera miden 13 px (0.8125rem) en la maqueta, no los 14 px de .boton-medio");

        var paquete = LeerHoja("Components", "BotonPaqueteEmpresa.razor.css");
        System.Text.RegularExpressions.Regex.IsMatch(paquete, @"\.paquete-destino\s*\{[^}]*font:\s*var\(--font-label-md\);")
            .Should().BeTrue("las pastillas de destino usan la letra de etiqueta de 13 px de la aplicación");
        System.Text.RegularExpressions.Regex.IsMatch(paquete, @"\.paquete-cifra\s*\{[^}]*font:\s*700 20px/26px var\(--font-family\);")
            .Should().BeTrue("la cifra de los recuentos usa la familia de la aplicación");
        System.Text.RegularExpressions.Regex.IsMatch(paquete, @"\.paquete-faltan-cabecera\s*\{[^}]*font:\s*600 12px/16px var\(--font-family\);")
            .Should().BeTrue("la cabecera «Faltan» usa la familia de la aplicación");
        System.Text.RegularExpressions.Regex.IsMatch(paquete, @"\.paquete-dialogo ::deep \.modal-contenido-grande\s*\{\s*max-width:\s*600px;")
            .Should().BeTrue("la maqueta (§ C) fija el diálogo en 600 px y Modal solo ofrece 420/520/720");
        System.Text.RegularExpressions.Regex.IsMatch(paquete, @"\.paquete-dialogo\s*\{\s*display:\s*contents;")
            .Should().BeTrue("el contenedor no puede ser un elemento más, con su gap, de la fila de acciones de la cabecera");
        paquete.Should().NotContain("Inter", "la aplicación no carga Inter: la familia viene de --font-family");
    }

    private static string LeerHoja(string carpeta, string fichero)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "Features", "Empresas", carpeta, fichero));
    }

    [Fact]
    public void Al_abrir_elige_el_primer_Cliente_empresarial_y_dice_que_va_que_falta_y_que_no_pide_antes_de_generar()
    {
        var mediador = Registrar(Empresa());

        var dialogo = AbrirPaquete(mediador).Find("[role=dialog]");

        mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Last().Should().Be(new ObtenerResumenPaqueteEmpresaQuery(EmpresaId, ClientePaqueteId));
        dialogo.QuerySelector("h2")!.TextContent.Trim().Should().Be("Preparar paquete documental");
        dialogo.QuerySelectorAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "true"), ("Grupo Arbeko", "false"), ("Todo lo vigente", "false"));
        dialogo.QuerySelectorAll(".paquete-recuento").Select(r => r.TextContent.Trim())
            .Should().Equal("31van en el paquete", "2faltan o vencidos", "9no los pide este Cliente");
        dialogo.QuerySelectorAll(".paquete-faltan li")
            .Select(l => System.Text.RegularExpressions.Regex.Replace(l.TextContent, @"\s+", " ").Trim())
            .Should().Equal("Seguro de Responsabilidad Civil Falta", "Certificado de aptitud médica · Paula Campos Lara Vencido 02/08");
        dialogo.TextContent.Should().Contain("Reclamar lo que falta");

        var descarga = ModuloDescarga(new BotonPaqueteEmpresa.RespuestaDescarga(true, 200, null));
        dialogo.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();
        descarga.Invocations["descargar"].Single().Arguments[0].Should().Be(
            $"/empresas/{EmpresaId}/paquete-documental.zip?cliente={ClientePaqueteId}",
            "la descarga va por el endpoint autenticado con el destino elegido, nunca por una URL pública");
    }

    [Fact]
    public void Todo_lo_vigente_pide_el_resumen_sin_destino_y_descarga_sin_Cliente()
    {
        var mediador = Registrar(Empresa());
        var descarga = ModuloDescarga(new BotonPaqueteEmpresa.RespuestaDescarga(true, 200, null));
        var cut = AbrirPaquete(mediador);

        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Todo lo vigente").Click();

        mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Last().ClienteEmpresarialId.Should().BeNull();
        cut.Find("[role=dialog]").TextContent.Should().NotContain("Reclamar lo que falta", "no falta nada");
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();
        descarga.Invocations["descargar"].Single().Arguments[0].Should().Be($"/empresas/{EmpresaId}/paquete-documental.zip");
    }

    [Theory]
    [InlineData(409, "El paquete pasa de 200 archivos. Elige un Cliente concreto para prepararlo.", "El paquete pasa de 200 archivos. Elige un Cliente concreto para prepararlo.")]
    [InlineData(403, "Tu rol no permite preparar ni descargar el paquete documental de una empresa — solo consultarla.", "Tu rol no permite preparar ni descargar el paquete documental de una empresa — solo consultarla.")]
    [InlineData(404, null, "La empresa o el destino ya no están disponibles para ti.")]
    [InlineData(0, null, "No pudimos generar el paquete. Inténtalo de nuevo.")]
    public void Si_el_servidor_rechaza_la_descarga_el_motivo_se_ve_en_el_dialogo(int estado, string? mensaje, string esperado)
    {
        var mediador = Registrar(Empresa());
        ModuloDescarga(new BotonPaqueteEmpresa.RespuestaDescarga(false, estado, mensaje));
        var cut = AbrirPaquete(mediador);

        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();

        cut.Find("[role=dialog] .paquete-error-descarga[role=alert] .paquete-error-motivo").TextContent.Trim().Should().Be(esperado,
            "un 409 o un 403 de la descarga no puede quedarse solo en la barra de descargas del navegador");
    }

    [Fact]
    public void El_error_de_la_descarga_es_la_tarjeta_de_la_maqueta_y_Reintentar_repite_la_misma_descarga()
    {
        // Maqueta «Mapa Dia a Dia TALVEG.dc.html» § C, «Barra de lote y estados»: «Error», «No pudimos preparar el paquete», el
        // motivo y «Reintentar». Se elige un destino que no es el inicial para que «el mismo destino» no salga gratis.
        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 0, null) };
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, Task.FromResult<IJSObjectReference>(modulo)));
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Grupo Arbeko").Click();
        var consultasAntes = mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Count();

        cut.Find("[role=dialog] .paquete-generar").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-error-descarga[role=alert]").Should().ContainSingle());
        var tarjeta = cut.Find("[role=dialog] .paquete-error-descarga");
        tarjeta.Children.Select(c => (c.ClassName, c.TextContent.Trim())).Should().Equal(
            ("paquete-error-etiqueta", "Error"),
            ("paquete-error-titulo", "No pudimos preparar el paquete"),
            ("paquete-error-motivo", "No pudimos generar el paquete. Inténtalo de nuevo."),
            ("paquete-error-acciones", "Reintentar"));

        modulo.Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(true, 200, null);
        cut.Find("[role=dialog] .paquete-reintentar").Click();

        cut.WaitForAssertion(() => modulo.Rutas.Should().HaveCount(2));
        modulo.Rutas.Should().OnlyContain(r => r == $"/empresas/{EmpresaId}/paquete-documental.zip?cliente={OtroClientePaqueteId}",
            "«Reintentar» repite la misma descarga, con el destino del resumen anunciado");
        mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Should().HaveCount(consultasAntes,
            "reintentar no vuelve a pedir el resumen ni cambia de destino");
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-error-descarga").Should().BeEmpty("la descarga salió bien"));
    }

    [Fact]
    public void Mientras_se_reintenta_la_tarjeta_y_su_boton_siguen_en_el_dialogo_y_un_fallo_nuevo_cambia_el_motivo()
    {
        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 0, null) };
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, Task.FromResult<IJSObjectReference>(modulo)));
        var cut = AbrirPaquete(Registrar(Empresa()));
        cut.Find("[role=dialog] .paquete-generar").Click();
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-reintentar").Should().ContainSingle());
        var espera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modulo.Retener = espera.Task;

        cut.Find("[role=dialog] .paquete-reintentar").Click();

        // El botón no sale del DOM ni se deshabilita: un botón con `disabled` pierde el foco, que caería al documento, fuera
        // del diálogo y de su trampa de foco. Lo que impide el segundo clic es aria-disabled y la guarda de GenerarAsync.
        var reintentar = cut.Find("[role=dialog] .paquete-error-descarga[role=alert] .paquete-reintentar");
        reintentar.HasAttribute("disabled").Should().BeFalse("el elemento con el foco no puede quedar deshabilitado");
        reintentar.GetAttribute("aria-disabled").Should().Be("true");
        reintentar.GetAttribute("aria-busy").Should().Be("true");
        reintentar.QuerySelectorAll(".paquete-reintentar-spinner[aria-hidden=true]").Should().ContainSingle();
        reintentar.Click();
        modulo.Rutas.Should().HaveCount(2, "no se reintenta dos veces a la vez");

        modulo.Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 409, "El paquete pasa de 200 archivos. Elige un Cliente concreto para prepararlo.");
        espera.SetResult();

        cut.WaitForAssertion(() => cut.Find("[role=dialog] .paquete-error-motivo").TextContent.Trim()
            .Should().Be("El paquete pasa de 200 archivos. Elige un Cliente concreto para prepararlo."));
        cut.Find("[role=dialog] .paquete-reintentar").HasAttribute("aria-disabled").Should().BeFalse();
        modulo.Rutas.Should().HaveCount(2);
    }

    [Fact]
    public void Si_el_reintento_vuelve_a_fallar_con_el_mismo_motivo_el_aviso_se_retira_y_se_vuelve_a_pintar()
    {
        // Con el mismo motivo, un alert cuyo texto no cambia no se vuelve a anunciar: durante el reintento el motivo sale del
        // alert y, al fallar otra vez, vuelve a entrar. Un solo alert, para no anunciarlo dos veces.
        const string generico = "No pudimos generar el paquete. Inténtalo de nuevo.";
        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 0, null) };
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, Task.FromResult<IJSObjectReference>(modulo)));
        var cut = AbrirPaquete(Registrar(Empresa()));
        cut.Find("[role=dialog] .paquete-generar").Click();
        cut.WaitForAssertion(() => cut.Find("[role=dialog] .paquete-error-motivo").TextContent.Trim().Should().Be(generico));
        var espera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modulo.Retener = espera.Task;

        cut.Find("[role=dialog] .paquete-reintentar").Click();

        cut.FindAll("[role=dialog] [role=alert] .paquete-error-motivo").Should().BeEmpty("mientras se reintenta el motivo se retira");

        espera.SetResult();

        cut.WaitForAssertion(() => cut.Find("[role=dialog] [role=alert] .paquete-error-motivo").TextContent.Trim().Should().Be(generico));
        cut.FindAll("[role=dialog] [role=alert]").Should().ContainSingle();
        modulo.Rutas.Should().HaveCount(2);
    }

    [Fact]
    public void Si_el_reintento_sale_bien_el_foco_pasa_al_cuerpo_del_dialogo_antes_de_retirar_la_tarjeta()
    {
        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 0, null) };
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, Task.FromResult<IJSObjectReference>(modulo)));
        var cut = AbrirPaquete(Registrar(Empresa()));
        cut.Find("[role=dialog] .paquete-generar").Click();
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-reintentar").Should().ContainSingle());
        modulo.Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(true, 200, null);

        cut.Find("[role=dialog] .paquete-reintentar").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-error-descarga").Should().BeEmpty());
        // «Reintentar» tenía el foco y se va con la tarjeta: sin esto el foco caería al documento, fuera del diálogo.
        var boton = cut.FindComponent<BotonPaqueteEmpresa>().Instance;
        var cuerpo = (ElementReference)typeof(BotonPaqueteEmpresa)
            .GetField("_cuerpo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(boton)!;
        JSInterop.VerifyFocusAsyncInvoke(1)[0].Arguments[0].Should().Be(cuerpo);
        cut.Find("[role=dialog] .paquete-cuerpo").GetAttribute("tabindex").Should().Be("-1", "el cuerpo tiene que poder recibir el foco");
    }

    [Fact]
    public void Si_se_cierra_el_dialogo_durante_el_reintento_no_se_mueve_el_foco_al_salir_bien()
    {
        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 0, null) };
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, Task.FromResult<IJSObjectReference>(modulo)));
        var cut = AbrirPaquete(Registrar(Empresa()));
        cut.Find("[role=dialog] .paquete-generar").Click();
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-reintentar").Should().ContainSingle());
        var espera = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        modulo.Retener = espera.Task;
        modulo.Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(true, 200, null);
        cut.Find("[role=dialog] .paquete-reintentar").Click();

        cut.Find("[role=dialog] .modal-cerrar").Click();
        espera.SetResult();

        // Cerrado, el foco ya volvió a quien abrió el diálogo: llevarlo a un cuerpo que no se ve se lo quitaría.
        cut.WaitForAssertion(() => cut.FindComponent<BotonPaqueteEmpresa>().Instance.GetType()
            .GetField("_generando", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.FindComponent<BotonPaqueteEmpresa>().Instance).Should().Be(false));
        JSInterop.Invocations.Where(i => i.Identifier.EndsWith(".focus", StringComparison.Ordinal)).Should().BeEmpty();
    }

    [Fact]
    public void Por_encima_de_200_archivos_no_ofrece_Generar_ZIP_y_explica_el_limite()
    {
        var mediador = Registrar(Empresa());
        mediador.ResumenPaquete = q => Result.Exito(new ResumenPaqueteEmpresaDto("Montajes Ebro SL", Destinos, q.ClienteEmpresarialId, 201, 0, 0, []));
        var cut = Renderizar();

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelectorAll("button").Should().NotContain(b => b.TextContent.Trim() == "Generar ZIP");
        dialogo.QuerySelector(".paquete-aviso")!.TextContent.Should().Contain("pasa de 200 archivos").And.Contain("Elige un Cliente concreto");
    }

    [Fact]
    public void Con_exactamente_200_archivos_si_ofrece_Generar_ZIP()
    {
        var mediador = Registrar(Empresa());
        mediador.ResumenPaquete = q => Result.Exito(new ResumenPaqueteEmpresaDto("Montajes Ebro SL", Destinos, q.ClienteEmpresarialId, 200, 0, 0, []));
        var cut = Renderizar();

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        cut.FindAll("[role=dialog] button").Should().Contain(b => b.TextContent.Trim() == "Generar ZIP");
    }

    [Fact]
    public void En_una_Sesion_Privilegiada_ve_el_resumen_pero_no_ofrece_Generar_ZIP()
    {
        var mediador = Registrar(Empresa());
        mediador.ResumenPaquete = q => Result.Exito(ResumenPaqueteDe(q.ClienteEmpresarialId) with { PuedeDescargar = false });
        var cut = Renderizar();

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelectorAll(".paquete-recuento").Should().HaveCount(3);
        dialogo.QuerySelectorAll("button").Should().NotContain(b => b.TextContent.Trim() == "Generar ZIP");
        dialogo.QuerySelector(".paquete-aviso")!.TextContent.Should().Contain("sesión privilegiada");
    }

    [Fact]
    public void Al_elegir_destino_los_botones_siguen_en_el_dialogo_y_el_resumen_nuevo_se_anuncia()
    {
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);

        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Grupo Arbeko").Click();

        cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "false"), ("Grupo Arbeko", "true"), ("Todo lo vigente", "false"));
        var recuentos = cut.Find(".paquete-recuentos");
        recuentos.GetAttribute("aria-live").Should().Be("polite");
        recuentos.GetAttribute("aria-atomic").Should().Be("true");
    }

    [Fact]
    public void Mientras_carga_el_destino_nuevo_se_queda_el_resumen_los_botones_siguen_y_Generar_esta_deshabilitado()
    {
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        var arbeko = cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Grupo Arbeko");
        var espera = new TaskCompletionSource();
        mediador.Retener = q => q is ObtenerResumenPaqueteEmpresaQuery ? espera.Task : null;

        arbeko.Click();

        cut.Find("[role=dialog]").TextContent.Should().NotContain("Calculando el paquete", "solo la primera carga sustituye el cuerpo");
        cut.Find(".paquete-cuerpo").GetAttribute("aria-busy").Should().Be("true");
        cut.FindAll(".paquete-destino").Should().HaveCount(3);
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").HasAttribute("disabled").Should().BeTrue(
            "mientras carga, lo anunciado aún es el destino anterior");

        espera.SetResult();

        cut.WaitForAssertion(() => cut.Find(".paquete-cuerpo").GetAttribute("aria-busy").Should().Be("false"));
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void La_respuesta_de_un_destino_ya_superado_no_pisa_el_resumen_del_destino_pulsado()
    {
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        mediador.ResumenPaquete = q => Result.Exito(new ResumenPaqueteEmpresaDto(
            "Montajes Ebro SL", Destinos, q.ClienteEmpresarialId, q.ClienteEmpresarialId == OtroClientePaqueteId ? 7 : 40, 0, 0, []));
        var espera = new TaskCompletionSource();
        mediador.Retener = q => q is ObtenerResumenPaqueteEmpresaQuery { ClienteEmpresarialId: var c } && c == OtroClientePaqueteId ? espera.Task : null;

        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Grupo Arbeko").Click();
        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Todo lo vigente").Click();
        espera.SetResult();

        cut.WaitForAssertion(() => cut.Find(".paquete-cuerpo").GetAttribute("aria-busy").Should().Be("false"));
        cut.Find(".paquete-recuento-incluye .paquete-cifra").TextContent.Trim().Should().Be("40",
            "la respuesta de Grupo Arbeko llegó tarde y se descarta: está pulsado «Todo lo vigente»");
    }

    [Fact]
    public void La_descarga_lleva_su_propio_limite_de_tiempo_y_no_el_de_60_segundos_de_la_interop()
    {
        // Sin token propio, Blazor Server corta la llamada a JS a los 60 s por defecto aunque el zip siga llegando.
        var importacion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, importacion.Task));
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        var modulo = new ModuloDescargaQueAnota();
        importacion.SetResult(modulo);

        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();

        cut.WaitForAssertion(() => modulo.Tokens.Should().ContainSingle());
        modulo.Tokens.Single().CanBeCanceled.Should().BeTrue("la llamada lleva el límite propio de la descarga");
        modulo.Tokens.Single().IsCancellationRequested.Should().BeFalse();
        BotonPaqueteEmpresa.TiempoMaximoDeDescarga.Should().BeGreaterThan(TimeSpan.FromMinutes(1),
            "un paquete de hasta 200 MiB no cabe en el minuto de la interop por defecto");
    }

    [Fact]
    public void Cambiar_de_destino_mientras_se_genera_no_cambia_el_paquete_que_se_descarga()
    {
        // La importación del módulo se queda pendiente: es la espera en la que, sin la corrección, se podía cambiar a «Todo lo
        // vigente» y descargar un paquete más amplio que el anunciado. Se registra antes de que nada lea los servicios.
        var importacion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, importacion.Task));
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        var consultasAntes = mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Count();

        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();

        var todo = cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Todo lo vigente");
        cut.FindAll(".paquete-destino").Should().OnlyContain(b => b.HasAttribute("disabled"), "mientras se genera, el destino no cambia");
        todo.Click();

        var modulo = new ModuloDescargaQueAnota();
        importacion.SetResult(modulo);
        cut.WaitForAssertion(() => cut.FindAll(".paquete-destino").Should().OnlyContain(b => !b.HasAttribute("disabled")));

        cut.WaitForAssertion(() => modulo.Rutas.Should().ContainSingle());
        modulo.Rutas.Single().Should().Be($"/empresas/{EmpresaId}/paquete-documental.zip?cliente={ClientePaqueteId}",
            "se descarga exactamente el paquete cuyo resumen se anunció al pulsar «Generar ZIP»");
        mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Should().HaveCount(consultasAntes, "el destino no cambió");
        cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "true"), ("Grupo Arbeko", "false"), ("Todo lo vigente", "false"));
        cut.FindAll(".paquete-destino").Should().OnlyContain(b => !b.HasAttribute("disabled"), "al terminar se puede volver a elegir");
    }

    [Fact]
    public void Cerrar_y_reabrir_el_dialogo_con_una_descarga_en_curso_sigue_anunciando_el_destino_que_se_descarga()
    {
        // «Todo lo vigente» es el paquete más amplio: reabrir sin conservarlo anunciaría el primer Cliente empresarial mientras
        // llega el ZIP de todo.
        var importacion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, importacion.Task));
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Todo lo vigente").Click();
        var consultasAntes = mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Count();
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();

        cut.Find("[role=dialog] .modal-cerrar").Click();
        cut.FindAll("[role=dialog]").Should().BeEmpty("el Gestor CAE cerró el diálogo");
        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "false"), ("Grupo Arbeko", "false"), ("Todo lo vigente", "true")); // el destino del ZIP que llega, no el primer Cliente empresarial
        cut.FindAll(".paquete-destino").Should().OnlyContain(b => b.HasAttribute("disabled"), "mientras se genera, el destino no cambia");
        mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Should().HaveCount(consultasAntes,
            "reabrir durante la descarga no vuelve a pedir ni a reiniciar el resumen");

        var modulo = new ModuloDescargaQueAnota();
        importacion.SetResult(modulo);
        cut.WaitForAssertion(() => modulo.Rutas.Should().ContainSingle());
        modulo.Rutas.Single().Should().Be($"/empresas/{EmpresaId}/paquete-documental.zip");
        cut.WaitForAssertion(() => cut.FindAll(".paquete-destino").Should().OnlyContain(b => !b.HasAttribute("disabled")));

        cut.Find("[role=dialog] .modal-cerrar").Click();
        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();
        cut.WaitForAssertion(() => cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "true"), ("Grupo Arbeko", "false"), ("Todo lo vigente", "false"))); // terminada la descarga, la siguiente apertura vuelve a elegir el primer Cliente empresarial
    }

    [Fact]
    public void Si_la_descarga_falla_con_el_dialogo_cerrado_el_motivo_se_ve_al_reabrirlo()
    {
        var importacion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, importacion.Task));
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Todo lo vigente").Click();
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Generar ZIP").Click();
        cut.Find("[role=dialog] .modal-cerrar").Click();

        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 409, "El paquete pasa de 200 archivos. Elige un Cliente concreto para prepararlo.") };
        importacion.SetResult(modulo);
        cut.WaitForAssertion(() => modulo.Rutas.Should().ContainSingle());
        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        cut.WaitForAssertion(() => cut.Find("[role=dialog] .paquete-error-descarga[role=alert] .paquete-error-motivo").TextContent.Trim()
            .Should().Be("El paquete pasa de 200 archivos. Elige un Cliente concreto para prepararlo."));
        // El diálogo reabre con el destino de la descarga que falló, no con el primer Cliente empresarial: el resumen y
        // «Reintentar» son los de ese paquete.
        cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "false"), ("Grupo Arbeko", "false"), ("Todo lo vigente", "true"));

        cut.Find("[role=dialog] .paquete-reintentar").Click();
        cut.WaitForAssertion(() => modulo.Rutas.Should().HaveCount(2));
        modulo.Rutas.Last().Should().Be($"/empresas/{EmpresaId}/paquete-documental.zip", "se reintenta «Todo lo vigente», lo que falló");
    }

    private (IRenderedComponent<EmpresaDetalle> Cut, MediatorFalso Mediador, ModuloDescargaQueAnota Modulo) FallarArbekoConElDialogoCerrado()
    {
        var importacion = new TaskCompletionSource<IJSObjectReference>(TaskCreationOptions.RunContinuationsAsynchronously);
        Services.AddSingleton<IJSRuntime>(new JsConImportacionRetenida(JSInterop.JSRuntime, importacion.Task));
        var mediador = Registrar(Empresa());
        var cut = AbrirPaquete(mediador);
        cut.FindAll(".paquete-destino").Single(b => b.TextContent.Trim() == "Grupo Arbeko").Click();
        cut.Find("[role=dialog] .paquete-generar").Click();
        cut.Find("[role=dialog] .modal-cerrar").Click();
        var modulo = new ModuloDescargaQueAnota { Respuesta = new BotonPaqueteEmpresa.RespuestaDescarga(false, 0, null) };
        importacion.SetResult(modulo);
        cut.WaitForAssertion(() => modulo.Rutas.Should().ContainSingle());
        return (cut, mediador, modulo);
    }

    [Fact]
    public void Si_falla_un_Cliente_concreto_con_el_dialogo_cerrado_al_reabrir_se_ve_y_se_reintenta_ese_Cliente()
    {
        var (cut, _, modulo) = FallarArbekoConElDialogoCerrado();

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        cut.WaitForAssertion(() => cut.FindAll("[role=dialog] .paquete-error-motivo").Should().ContainSingle());
        cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "false"), ("Grupo Arbeko", "true"), ("Todo lo vigente", "false"));
        cut.Find("[role=dialog] .paquete-reintentar").Click();
        cut.WaitForAssertion(() => modulo.Rutas.Should().HaveCount(2));
        modulo.Rutas.Last().Should().Be($"/empresas/{EmpresaId}/paquete-documental.zip?cliente={OtroClientePaqueteId}");
    }

    [Fact]
    public void Si_el_Cliente_que_fallo_ya_no_se_puede_consultar_al_reabrir_el_dialogo_abre_como_siempre()
    {
        var (cut, mediador, _) = FallarArbekoConElDialogoCerrado();
        mediador.ResumenPaquete = q => q.ClienteEmpresarialId == OtroClientePaqueteId
            ? Result.Fallo<ResumenPaqueteEmpresaDto>(Error.Crear("NoEncontrada", "No encontrada."))
            : Result.Exito(ResumenPaqueteDe(q.ClienteEmpresarialId));

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        // Sin la vuelta a la apertura normal, el error de carga ofrecería un «Reintentar» que pide siempre el mismo destino.
        cut.WaitForAssertion(() => cut.FindAll(".paquete-destino").Select(b => (b.TextContent.Trim(), b.GetAttribute("aria-pressed")))
            .Should().Equal(("Refrielectric S.A.", "true"), ("Grupo Arbeko", "false"), ("Todo lo vigente", "false")));
        cut.FindAll("[role=dialog] .paquete-error-descarga").Should().BeEmpty("ese paquete ya no se puede reintentar");
    }

    [Fact]
    public async Task Una_apertura_anterior_que_termina_tarde_no_toca_el_error_de_la_apertura_actual()
    {
        var (cut, mediador, _) = FallarArbekoConElDialogoCerrado();
        // La carga del destino que falló (Grupo Arbeko) queda retenida y fallará; la de la apertura nueva falla la primera vez.
        var arbeko = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retener = q => q is ObtenerResumenPaqueteEmpresaQuery { ClienteEmpresarialId: var id } && id == OtroClientePaqueteId ? arbeko.Task : null;
        var fallosDeTodo = 0;
        var arbekoRespondido = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.ResumenPaquete = q =>
        {
            if (q.ClienteEmpresarialId == OtroClientePaqueteId) arbekoRespondido.TrySetResult();
            return q.ClienteEmpresarialId == OtroClientePaqueteId || (q.ClienteEmpresarialId is null && fallosDeTodo++ == 0)
                ? Result.Fallo<ResumenPaqueteEmpresaDto>(Error.Crear("NoEncontrada", "No encontrada."))
                : Result.Exito(ResumenPaqueteDe(q.ClienteEmpresarialId));
        };
        var preparar = () => cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        preparar();
        cut.Find("[role=dialog] .modal-cerrar").Click();
        preparar();
        cut.WaitForAssertion(() => cut.Find("[role=dialog] .paquete-estado[role=alert]").TextContent.Should().Contain("No encontrada."));
        var consultasAntes = mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Count();

        arbeko.SetResult();
        await arbekoRespondido.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // La respuesta y la continuación de la primera apertura corren en el mismo turno del dispatcher: un turno vacío después
        // garantiza que ya terminó.
        await cut.InvokeAsync(() => { });

        // La continuación de la primera apertura no puede leer el error de esta como suyo ni lanzar la apertura normal encima.
        mediador.Enviadas.OfType<ObtenerResumenPaqueteEmpresaQuery>().Should().HaveCount(consultasAntes);
        cut.Find("[role=dialog] .paquete-estado[role=alert]").TextContent.Should().Contain("No encontrada.");
        cut.FindAll(".paquete-destino").Should().BeEmpty();
    }

    /// <summary>
    /// El JS de bUnit, salvo la importación de <c>descarga-archivo.js</c>, que espera a <paramref name="modulo"/> (bUnit no
    /// deja retener un <c>SetupModule</c>).
    /// </summary>
    private sealed class JsConImportacionRetenida(IJSRuntime interior, Task<IJSObjectReference> modulo) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            identifier == "import" && args is ["./js/descarga-archivo.js", ..]
                ? EsperarModuloAsync<TValue>()
                : interior.InvokeAsync<TValue>(identifier, cancellationToken, args);

        private async ValueTask<TValue> EsperarModuloAsync<TValue>() => (TValue)await modulo;
    }

    /// <summary>El módulo <c>descarga-archivo.js</c> con la importación retenida: anota cada ruta que se pide.</summary>
    private sealed class ModuloDescargaQueAnota : IJSObjectReference
    {
        public BotonPaqueteEmpresa.RespuestaDescarga Respuesta { get; set; } = new(true, 200, null);

        /// <summary>Si está puesta, la llamada siguiente espera a que termine y devuelve la <see cref="Respuesta"/> de ese momento.</summary>
        public Task? Retener { get; set; }

        public List<string> Rutas { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            identifier.Should().Be("descargar");
            Tokens.Add(cancellationToken);
            Rutas.Add((string)args![0]!);
            if (Retener is { } espera)
            {
                Retener = null;
                return new ValueTask<TValue>(espera.ContinueWith(_ => (TValue)(object)Respuesta, TaskScheduler.Default));
            }
            return ValueTask.FromResult((TValue)(object)Respuesta);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private BunitJSModuleInterop ModuloDescarga(BotonPaqueteEmpresa.RespuestaDescarga respuesta)
    {
        var modulo = JSInterop.SetupModule("./js/descarga-archivo.js");
        modulo.Setup<BotonPaqueteEmpresa.RespuestaDescarga>("descargar", _ => true).SetResult(respuesta);
        return modulo;
    }

    [Fact]
    public void Si_nada_esta_vigente_no_ofrece_generar_y_si_reclamar_lo_que_falta()
    {
        var mediador = Registrar(Empresa());
        mediador.ResumenPaquete = q => Result.Exito(new ResumenPaqueteEmpresaDto("Montajes Ebro SL", [], null, 0, 0, 0, []));
        var cut = Renderizar();

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelectorAll("button, a").Should().NotContain(a => a.TextContent.Trim() == "Generar ZIP");
        dialogo.TextContent.Should().Contain("no se genera el paquete").And.Contain("Reclamar lo que falta");
    }

    [Fact]
    public void Si_la_consulta_rechaza_el_paquete_lo_dice_y_no_ofrece_generar()
    {
        var mediador = Registrar(Empresa());
        mediador.ResumenPaquete = _ => Result.Fallo<ResumenPaqueteEmpresaDto>(AutorizacionPaqueteEmpresa.RolSinDescarga);
        var cut = Renderizar();

        cut.FindAll(".cabecera-identidad-acciones button").Single(b => b.TextContent.Trim() == "Preparar paquete").Click();

        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector("[role=alert]")!.TextContent.Should().Contain(AutorizacionPaqueteEmpresa.RolSinDescarga.Mensaje);
        dialogo.QuerySelectorAll("button, a").Should().NotContain(a => a.TextContent.Trim() == "Generar ZIP");
    }

    /// <summary>
    /// Un dato opcional sin valor no se pinta —ni su etiqueta ni un «—»— en la cabecera ni en la tarjeta de información del lateral.
    /// Hasta ahora solo lo cubrían los tests del panel; esta es la página.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void El_CIF_el_CNAE_y_el_convenio_vacios_no_se_pintan_en_la_cabecera_ni_en_el_lateral(string? vacio)
    {
        var mediador = Empresa();
        mediador.Detalles[EmpresaId] = mediador.Detalles[EmpresaId] with { Cif = vacio, Cnae = vacio, ConvenioAplicable = vacio };
        Registrar(mediador);

        var cut = Renderizar();

        var cabecera = cut.Find(".cabecera-identidad-datos").TextContent;
        var lateral = cut.Find(".cuerpo-con-lateral-lateral").TextContent;
        cabecera.Should().NotContain("CIF");
        lateral.Should().NotContain("Identificación fiscal").And.NotContain("CNAE").And.NotContain("Convenio aplicable");
        // La tarjeta sigue viva con lo que sí se tiene.
        lateral.Should().Contain("Alta").And.Contain("Actividad de Anexo I");
    }

    [Fact]
    public void Con_CIF_CNAE_y_convenio_la_cabecera_y_el_lateral_los_pintan()
    {
        // Control del instrumento: sin él, el test de arriba pasaría aunque la página no pintara nunca estos datos.
        Registrar(Empresa());

        var cut = Renderizar();

        cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("CIF").And.Contain("B-48.220.917");
        cut.Find(".cuerpo-con-lateral-lateral").TextContent
            .Should().Contain("Identificación fiscal").And.Contain("CNAE").And.Contain("4322").And.Contain("Convenio aplicable").And.Contain("Metal de Bizkaia");
    }

    [Fact]
    public void La_informacion_del_lateral_sale_del_detalle()
    {
        Registrar(Empresa());

        var lateral = Renderizar().Find(".cuerpo-con-lateral-lateral").TextContent;

        lateral.Should().Contain("4322").And.Contain("14/03/2025").And.Contain("Metal de Bizkaia").And.Contain("Sí");
    }

    [Fact]
    public void Si_la_empresa_no_llega_pinta_FichaNoEncontrada_sin_Reintentar_y_no_se_consulta_nada_mas()
    {
        ComponentFactories.AddStub<FichaNoEncontrada>();
        var mediador = Registrar(new MediatorFalso());

        var cut = Renderizar();

        cut.RutaListaDeFichaNoEncontrada().Should().Be("/empresas");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Reintentar");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerCredencialAccesoEmpresaSinContrasenaQuery>().Should().BeEmpty();
    }

    // ── Indicadores y chips ───────────────────────────────────────────────

    [Fact]
    public void La_cabecera_no_pinta_indicadores_aunque_haya_incidencias()
    {
        Registrar(Empresa());

        var cut = Renderizar();

        var cabecera = cut.Find("[data-pieza=\"cabecera-identidad\"]").TextContent;
        cabecera.Should().NotContain("trabajadores sin documentación válida").And.NotContain("trabajadores con documentos por vencer");
        cut.FindAll(".ventana-contexto").Should().BeEmpty();
        Chips(cut).Should().Equal(["Todos · 25", "Sin documentación válida · 3", "Por vencer · 3", "Al día · 19"]);
    }

    /// <summary>
    /// Revisión 1:1 (2026-10-21): Empresa 360 pintaba «Sin incidencias» y contaba «Al día» a quien el listado daba como
    /// Pendiente (Andrés Blanco, Elena Cano…). La maqueta del 20-10 define «sin documentación válida» como Bloqueado +
    /// Vencido + Pendiente: el Pendiente entra en ese chip, su fila dice «Pendiente» y ya no hay aclaración de lo que no cuenta.
    /// </summary>
    [Fact]
    public async Task El_pendiente_cuenta_como_sin_documentacion_valida_y_su_fila_dice_Pendiente()
    {
        var mediador = Empresa(
        [
            Trabajador(EstadoDocumento.Vencido, "Vencido Uno"),
            Trabajador(EstadoDocumento.Faltante, "Blanco"),
            Trabajador(EstadoDocumento.Faltante, "Cano"),
            Trabajador(EstadoDocumento.Urgente, "Urgente Uno"),
            Trabajador(EstadoDocumento.Vigente, "Vigente Uno"),
            Trabajador(EstadoDocumento.SinCaducidad, "Sin caducidad Uno")
        ]);
        Registrar(mediador);

        var cut = Renderizar();

        Chips(cut).Should().Equal(["Todos · 6", "Sin documentación válida · 3", "Por vencer · 1", "Al día · 2"]);
        cut.FindAll(".empresa360-chip").Should().OnlyContain(c => !c.HasAttribute("title"));

        await cut.FindAll(".empresa360-chip")[1].ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => EstadosDeLasFilas(cut).Should().BeEquivalentTo(["Vencido", "Pendiente", "Pendiente"]));
    }

    /// <summary>
    /// Pulido de la demo (2026-11-10): «Al día · 7» contradecía filas «Sin confirmar». Por D-22 (2026-10-01) lo Sin confirmar
    /// cuenta como al día con aviso; el chip lo dice, con el recuento tomado de la misma consulta (EstadoDocumental) que las filas.
    /// </summary>
    [Fact]
    public void El_chip_Al_dia_dice_cuantos_de_ellos_estan_sin_confirmar_y_sin_ninguno_no_dice_nada()
    {
        Registrar(Empresa(
        [
            Trabajador(EstadoDocumento.Vencido, "Vencido Uno"),
            Trabajador(EstadoDocumento.SinConfirmar, "Sin confirmar Uno"),
            Trabajador(EstadoDocumento.SinConfirmar, "Sin confirmar Dos"),
            Trabajador(EstadoDocumento.Vigente, "Vigente Uno")
        ]));

        Chips(Renderizar()).Should().Contain(c => c.StartsWith("Al día · 3 (de ellos, 2 sin confirmar)"));
    }

    [Fact]
    public void Un_solo_trabajador_se_glosa_en_singular_en_la_pestana()
    {
        Registrar(Empresa([Trabajador(EstadoDocumento.Vigente, "Vigente Uno")]));

        var glosas = Renderizar().FindAll(".pestanas-contador-glosa").Select(g => g.TextContent.Trim()).ToList();

        glosas.Should().Contain("trabajador").And.NotContain("trabajadores");
    }

    [Fact]
    public async Task Pulsar_el_chip_sin_documentacion_valida_filtra_la_lista_y_lo_deja_en_la_url()
    {
        Registrar(Empresa());
        var cut = Renderizar();

        await cut.FindAll(".empresa360-chip")[1].ClickAsync(new MouseEventArgs());

        ChipPulsado(cut).Should().StartWith("Sin documentación válida");
        cut.WaitForAssertion(() => EstadosDeLasFilas(cut).Should().Equal(["Vencido", "Vencido", "Vencido"]));
        Navegacion.Uri.Should().Contain("estado=sin-documentacion-valida");
    }

    [Fact]
    public void El_estado_de_la_URL_marca_el_chip_por_vencer_y_filtra_la_lista()
    {
        Registrar(Empresa());

        var cut = Renderizar("?estado=por-vencer");

        cut.WaitForAssertion(() => EstadosDeLasFilas(cut).Should().Equal(["Por vencer", "Por vencer", "Por vencer"]));
        ChipPulsado(cut).Should().StartWith("Por vencer");
    }

    [Fact]
    public void Todos_ordena_del_peor_estado_al_mejor_y_pagina_de_20_en_20()
    {
        var mediador = Registrar(Empresa());

        var cut = Renderizar();

        var estados = EstadosDeLasFilas(cut);
        estados.Should().HaveCount(20);
        estados.Take(6).Should().Equal(["Vencido", "Vencido", "Vencido", "Por vencer", "Por vencer", "Por vencer"]);
        EstadosDeCodigoDeLasFilas(cut, mediador).Take(6).Should().Equal(
            [EstadoDocumento.Vencido, EstadoDocumento.Vencido, EstadoDocumento.Vencido, EstadoDocumento.Urgente, EstadoDocumento.Urgente, EstadoDocumento.Proximo],
            "los dos se rotulan «Por vencer», pero lo urgente sigue yendo delante de lo próximo");
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 1 de 2");
    }

    [Fact]
    public async Task La_segunda_pagina_trae_el_resto_sin_repetir_filas()
    {
        Registrar(Empresa());
        var cut = Renderizar();
        var primera = cut.FindAll(".fila-relacion-nombre").Select(a => a.GetAttribute("href")).ToList();

        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());

        var segunda = cut.FindAll(".fila-relacion-nombre").Select(a => a.GetAttribute("href")).ToList();
        segunda.Should().HaveCount(5).And.NotIntersectWith(primera);
    }

    [Theory]
    [InlineData("Por vencer", "por-vencer", new[] { "Por vencer", "Por vencer", "Por vencer" },
        new[] { EstadoDocumento.Urgente, EstadoDocumento.Urgente, EstadoDocumento.Proximo })]
    [InlineData("Sin documentación válida", "sin-documentacion-valida", new[] { "Vencido", "Vencido", "Vencido" },
        new[] { EstadoDocumento.Vencido, EstadoDocumento.Vencido, EstadoDocumento.Vencido })]
    public async Task Cada_chip_enseña_solo_su_tramo_y_viaja_en_la_url(
        string chip, string enUrl, string[] rotulosEsperados, EstadoDocumento[] estadosEsperados)
    {
        var mediador = Registrar(Empresa());
        var cut = Renderizar();

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.StartsWith(chip, StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        EstadosDeLasFilas(cut).Should().Equal(rotulosEsperados);
        EstadosDeCodigoDeLasFilas(cut, mediador).Should().Equal(estadosEsperados.Cast<EstadoDocumento?>());
        Navegacion.Uri.Should().Contain($"estado={enUrl}");
    }

    [Fact]
    public async Task Al_dia_cruza_la_frontera_de_pagina_de_la_lista_ordenada_sin_perder_filas()
    {
        var mediador = Registrar(Empresa());
        var cut = Renderizar();

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.StartsWith("Al día", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        // Tramo [6, 25) de la lista ordenada: 14 filas de su página 1 y 5 de la 2. Vigente y Sin caducidad se
        // rotulan los dos «Sin incidencias»; el orden dentro del tramo se comprueba con el estado de código.
        EstadosDeLasFilas(cut).Should().HaveCount(19).And.OnlyContain(e => e == "Sin incidencias");
        var estados = EstadosDeCodigoDeLasFilas(cut, mediador);
        estados.Take(12).Should().OnlyContain(e => e == EstadoDocumento.Vigente, "dentro del tramo también manda el orden");
        estados.Skip(12).Should().HaveCount(7).And.OnlyContain(e => e == EstadoDocumento.SinCaducidad);
        cut.FindAll(".paginador").Should().BeEmpty("19 caben en una página");
    }

    /// <summary>
    /// Con un chip de más de una página, cada página tiene exactamente 20 filas
    /// de su tramo. El filtro defensivo por estado quita una fila de otro
    /// estado que se cuele, así que un tramo desplazado en uno no cambia el
    /// total visible: solo lo delata que la primera página se quede en 19.
    /// </summary>
    [Fact]
    public async Task Un_chip_de_varias_paginas_llena_cada_pagina_con_su_tramo()
    {
        var mediador = Registrar(Empresa([
            .. Enumerable.Range(0, 3).Select(_ => Trabajador(EstadoDocumento.Vencido)),
            Trabajador(EstadoDocumento.Proximo),
            .. Enumerable.Range(0, 30).Select(_ => Trabajador(EstadoDocumento.Vigente)),
        ]));
        var cut = Renderizar("?estado=al-dia");

        EstadosDeLasFilas(cut).Should().HaveCount(20).And.OnlyContain(e => e == "Sin incidencias");
        EstadosDeCodigoDeLasFilas(cut, mediador).Should().HaveCount(20).And.OnlyContain(e => e == EstadoDocumento.Vigente);
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 1 de 2").And.Contain("30");
        cut.Find(".empresa360-pie").TextContent.Trim().Should().Be("Mostrando 20 de 30 · del peor estado al mejor");

        await cut.FindAll(".paginador button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());

        EstadosDeLasFilas(cut).Should().HaveCount(10).And.OnlyContain(e => e == "Sin incidencias");
        EstadosDeCodigoDeLasFilas(cut, mediador).Should().HaveCount(10).And.OnlyContain(e => e == EstadoDocumento.Vigente);
    }

    [Fact]
    public void El_chip_de_la_url_se_adopta_al_entrar()
    {
        var mediador = Registrar(Empresa());

        var cut = Renderizar("?estado=por-vencer");

        ChipPulsado(cut).Should().StartWith("Por vencer");
        EstadosDeLasFilas(cut).Should().Equal(["Por vencer", "Por vencer", "Por vencer"]);
        EstadosDeCodigoDeLasFilas(cut, mediador).Should().Equal(EstadoDocumento.Urgente, EstadoDocumento.Urgente, EstadoDocumento.Proximo);
    }

    // ── Pestañas ──────────────────────────────────────────────────────────

    [Fact]
    public void La_tarjeta_Se_pide_la_documentacion_a_muestra_el_contacto_predeterminado_sin_sus_notas()
    {
        var mediador = Empresa();
        mediador.Agenda[EmpresaId] =
        [
            new ContactoAgendaDto(Guid.NewGuid(), "Iker Mena", "iker.mena@ejemplo.test", "610338921", "Jefe de equipo", null, false, false, false, [], [], []),
            new ContactoAgendaDto(Guid.NewGuid(), "Jon Arregi", "jon.arregi@ejemplo.test", "976338900", "Administración", "Nota interna del contacto", true, false, false, [], [], [])
        ];
        Registrar(mediador);
        var cut = Renderizar();
        var lateral = cut.Find(".cuerpo-con-lateral-lateral");
        lateral.TextContent.Should().Contain("Se pide la documentación a").And.Contain("Jon Arregi").And.Contain("Administración").And.Contain("contacto principal")
            .And.Contain("Recibe las reclamaciones de documentos de esta Empresa.");
        lateral.TextContent.Should().NotContain("Iker Mena").And.NotContain("Nota interna del contacto");
        // El nombre accesible contiene el dato visible (WCAG 2.5.3) para que el control por voz lo active.
        lateral.QuerySelectorAll(".empresa360-contacto-filas button").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal("Copiar correo jon.arregi@ejemplo.test", "Copiar teléfono 976338900");
    }

    [Fact]
    public void Sin_contacto_predeterminado_no_se_pinta_la_tarjeta_Se_pide_la_documentacion()
    {
        var mediador = Empresa();
        mediador.Agenda[EmpresaId] = [new ContactoAgendaDto(Guid.NewGuid(), "Iker Mena", "iker.mena@ejemplo.test", null, null, null, false, false, false, [], [], [])];
        Registrar(mediador);
        var cut = Renderizar();
        cut.Find(".cuerpo-con-lateral-lateral").TextContent.Should().NotContain("Se pide la documentación a");
    }

    [Fact]
    public void El_enlace_Agenda_de_la_tarjeta_abre_la_pestana_Agenda()
    {
        var mediador = Empresa();
        mediador.Agenda[EmpresaId] = [new ContactoAgendaDto(Guid.NewGuid(), "Jon Arregi", "jon.arregi@ejemplo.test", null, "Administración", null, true, false, false, [], [], [])];
        Registrar(mediador);
        var cut = Renderizar();
        cut.Find(".cuerpo-con-lateral-lateral").QuerySelectorAll("button").Single(b => b.TextContent.Contains("Agenda")).Click();
        Pestana(cut, "Agenda").GetAttribute("aria-selected").Should().Be("true");
    }

    [Fact]
    public void Las_pestanas_siguen_el_orden_del_mockup_y_Trabajadores_es_la_de_entrada()
    {
        var mediador = Empresa();
        mediador.Clientes[EmpresaId] =
        [
            new ClienteDeEmpresaDto(Guid.NewGuid(), "Refrielectric S.A.", "A-12345678"),
            new ClienteDeEmpresaDto(Guid.NewGuid(), "Aislamientos Nervión S.L.", "B-87654321")
        ];
        Registrar(mediador);

        var cut = Renderizar();

        cut.FindAll("[role=tab]").Select(t => t.ChildNodes.OfType<IText>().First(n => !string.IsNullOrWhiteSpace(n.Data)).Data.Trim())
            .Should().Equal(["Trabajadores", "Vigencia", "Clientes", "Documentación", "Reclamar", "Plataformas", "Agenda", "Ficha", "Historial"]);
        Pestana(cut, "Trabajadores").GetAttribute("aria-selected").Should().Be("true");
        Pestana(cut, "Trabajadores").TextContent.Should().Contain("25");
        Pestana(cut, "Clientes").TextContent.Should().Contain("2");
    }

    [Fact]
    public async Task Cambiar_de_pestana_la_deja_en_la_url_y_monta_el_componente_de_la_empresa()
    {
        Registrar(Empresa());
        var cut = Renderizar();

        await Pestana(cut, "Ficha").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().Contain("pestana=ficha");
        cut.FindComponent<Stub<PestanaSelloEmpresa>>().Instance.Parameters.Get(x => x.EntidadId).Should().Be(EmpresaId);

        await Pestana(cut, "Trabajadores").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().NotContain("pestana=", "Trabajadores es la pestaña por defecto: no ensucia la URL");
    }

    [Theory]
    [InlineData("documentacion")]
    [InlineData("agenda")]
    [InlineData("historial")]
    [InlineData("vigencia")]
    [InlineData("plataformas")]
    [InlineData("reclamar")]
    public void La_pestana_de_la_url_monta_su_componente_con_la_empresa(string pestana)
    {
        Registrar(Empresa());

        var cut = Renderizar($"?pestana={pestana}");

        Guid propietario = pestana switch
        {
            "documentacion" => cut.FindComponent<Stub<PestanaDocumentacion>>().Instance.Parameters.Get(x => x.PropietarioId),
            "agenda" => cut.FindComponent<Stub<PestanaAgendaContactos>>().Instance.Parameters.Get(x => x.PropietarioId),
            "vigencia" => cut.FindComponent<Stub<PestanaVigenciaEmpresa>>().Instance.Parameters.Get(x => x.EmpresaId),
            "plataformas" => cut.FindComponent<Stub<PestanaPlataformasEmpresa>>().Instance.Parameters.Get(x => x.EmpresaId),
            "reclamar" => cut.FindComponent<Stub<PestanaReclamarEmpresa>>().Instance.Parameters.Get(x => x.EmpresaId),
            _ => cut.FindComponent<Stub<PestanaHistorial>>().Instance.Parameters.Get(x => x.EntidadId)
        };
        propietario.Should().Be(EmpresaId);
    }

    [Fact]
    public void El_enlace_antiguo_sello_abre_Ficha_con_el_sello_de_la_empresa()
    {
        Registrar(Empresa());
        var cut = Renderizar("?pestana=sello");
        Pestana(cut, "Ficha").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[role=tab]").Should().NotContain(t => t.TextContent.Trim() == "Sello");
        cut.FindComponent<Stub<PestanaSelloEmpresa>>().Instance.Parameters.Get(x => x.EntidadId).Should().Be(EmpresaId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?pestana=ficha")]
    [InlineData("?pestana=sello")]
    public void Sin_datos_opcionales_la_pestana_Ficha_conserva_las_cajas_de_empresa(string consulta)
    {
        var m = Empresa();
        m.Sellos.Clear();
        Registrar(m, Roles.Consulta);
        var cut = Renderizar(consulta);
        cut.FindAll("[role=tab]").Should().Contain(t => t.TextContent.Trim() == "Ficha");
        if (!string.IsNullOrEmpty(consulta))
        {
            Pestana(cut, "Ficha").GetAttribute("aria-selected").Should().Be("true");
            cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Should().HaveCount(4);
        }
        else
        {
            Pestana(cut, "Trabajadores").GetAttribute("aria-selected").Should().Be("true",
                "sin ?pestana= la página abre en Trabajadores aunque haya cajas de ficha");
        }
    }

    [Fact]
    public void El_lateral_solo_tiene_Informacion_y_Ficha_agrupa_las_cajas_cargadas()
    {
        EmpresaConCredencial();
        var cut = Renderizar("?pestana=ficha");
        var lateral = cut.Find(".cuerpo-con-lateral-lateral");
        lateral.QuerySelectorAll(".tarjeta").Should().ContainSingle();
        lateral.TextContent.Should().Contain("Información").And.NotContain("Carpeta de documentación");
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("acceso", "firma-documentos", "prevencion", "mutua", "formacion");
        var tabs = cut.FindAll("[role=tab]");
        tabs[^2].TextContent.Trim().Should().Be("Ficha");
        tabs[^1].TextContent.Trim().Should().Be("Historial");
    }

    [Fact]
    public void El_lateral_con_nota_visible_tiene_Informacion_y_Nota_interna_en_ese_orden()
    {
        var m = Empresa();
        ConNota(m, NotaGuardada);
        Registrar(m);
        var cut = Renderizar();
        var lateral = cut.Find(".cuerpo-con-lateral-lateral");
        lateral.QuerySelectorAll(".tarjeta").Should().HaveCount(2);
        var texto = lateral.TextContent;
        texto.IndexOf("Información", StringComparison.Ordinal).Should().BeGreaterThanOrEqualTo(0);
        texto.IndexOf("Nota interna", StringComparison.Ordinal).Should().BeGreaterThan(texto.IndexOf("Información", StringComparison.Ordinal));
        lateral.QuerySelector("[data-pieza=nota-interna]").Should().NotBeNull();
    }

    [Fact]
    public void Ficha_con_solo_acceso_no_monta_una_caja_de_sello_vacia()
    {
        var m = EmpresaConCredencial(rol: Roles.Consulta);
        m.Sellos.Clear();
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("acceso", "firma-documentos", "prevencion", "mutua", "formacion");
        cut.FindComponents<Stub<PestanaSelloEmpresa>>().Should().BeEmpty();
        cut.Markup.Should().Contain("https://app.twind.io/login").And.NotContain("usuario.ibertec");
    }

    [Fact]
    public void Con_escritura_y_sin_sello_la_Ficha_monta_la_caja_Sello_con_su_zona_de_subida()
    {
        var m = Empresa();
        m.Sellos.Clear();
        Registrar(m);
        var cut = Renderizar("?pestana=ficha");
        Pestana(cut, "Ficha").GetAttribute("aria-selected").Should().Be("true");
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("firma-documentos", "prevencion", "mutua", "formacion");
        cut.FindComponent<Stub<PestanaSelloEmpresa>>().Instance.Parameters.Get(x => x.EntidadId).Should().Be(EmpresaId);
        cut.Find("[data-pieza=sello-empresa] h2").TextContent.Should().Be("Sello", "la maqueta lo pone como sección bajo las cajas, no como caja del flujo");
    }

    [Fact]
    public void Con_escritura_sin_sello_y_con_acceso_la_Ficha_monta_las_dos_cajas()
    {
        var m = EmpresaConCredencial();
        m.Sellos.Clear();
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("acceso", "firma-documentos", "prevencion", "mutua", "formacion");
        cut.FindComponent<Stub<PestanaSelloEmpresa>>().Instance.Parameters.Get(x => x.EntidadId).Should().Be(EmpresaId);
    }

    [Fact]
    public void El_enlace_a_Ficha_sin_datos_opcionales_conserva_las_cajas_de_empresa()
    {
        var m = Empresa();
        m.Sellos.Clear();
        Registrar(m, Roles.Consulta);
        var cut = Renderizar();
        Navegacion.NavigateTo($"/empresas/{EmpresaId}?pestana=ficha");
        cut.WaitForAssertion(() => Pestana(cut, "Ficha").GetAttribute("aria-selected").Should().Be("true"));
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Should().HaveCount(4);
    }

    // ── Filas: enlace a la página y 360 al panel ──────────────────────────

    [Fact]
    public async Task La_fila_de_trabajador_enlaza_a_su_pagina_y_el_360_abre_su_panel()
    {
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var unico = Trabajador(EstadoDocumento.Vigente, "Ruiz");
        Registrar(Empresa([unico]));
        var cut = Renderizar();

        var nombre = cut.Find(".fila-relacion-nombre");
        nombre.GetAttribute("href").Should().Be($"/trabajadores/{unico.Id}");
        nombre.GetAttribute("title").Should().Be("Ir a la página de Ana Ruiz");
        // Maqueta nueva: el propio DNI es el único control de copia en la fila.
        cut.Find(".fila-relacion-avatar").TextContent.Trim().Should().Be("AR");
        cut.FindAll(".fila-relacion-icono").Should().BeEmpty();
        var detalle = cut.Find(".fila-relacion-detalle");
        detalle.TextContent.Trim().Should().Be($"DNI {unico.Dni}");
        var copiar = detalle.QuerySelectorAll(".boton-copiar").Should().ContainSingle().Subject;
        copiar.TextContent.Should().Be(unico.Dni);
        copiar.ClassList.Should().Contain("boton-copiar-en-linea");
        copiar.GetAttribute("title").Should().Be($"Copiar {unico.Dni}");
        copiar.GetAttribute("aria-label").Should().Be($"Copiar {unico.Dni}");
        await copiar.ClickAsync(new MouseEventArgs());
        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal(unico.Dni);
        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(t => t.Tono == TonoToast.Exito && t.Mensaje.Contains("el DNI"));
        cut.Find(".empresa360-pie").TextContent.Trim().Should().Be("Mostrando 1 de 1 · del peor estado al mejor");

        await cut.Find(".fila-relacion .boton-360").ClickAsync(new MouseEventArgs());

        Workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Trabajador, unico.Id, "Ana Ruiz", "informacion"));
    }

    [Fact]
    public async Task La_pestana_Clientes_se_carga_al_abrirla_y_sus_filas_enlazan_y_abren_panel()
    {
        var mediador = Empresa();
        var cliente = new ClienteDeEmpresaDto(Guid.NewGuid(), "Refrielectric S.A.", "A-12345678");
        mediador.Clientes[EmpresaId] = [cliente];
        Registrar(mediador);
        var cut = Renderizar();

        await Pestana(cut, "Clientes").ClickAsync(new MouseEventArgs());

        // Se piden una sola vez, al cargar la página (de ahí sale el recuento de la
        // cabecera); abrir la pestaña no repite la consulta.
        mediador.Enviadas.OfType<ObtenerClientesDeEmpresaQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerClientesDeEmpresaQuery(EmpresaId));
        cut.Find(".fila-relacion-nombre").GetAttribute("href").Should().Be($"/clientes/{cliente.Id}");
        cut.Find(".fila-relacion-detalle").TextContent.Trim().Should().Be("A-12345678", "el mockup pone el CIF solo, sin prefijo");
        Pestana(cut, "Clientes").TextContent.Should().Contain("1", "el contador pasa a ser lo que la lista enseña");

        await cut.Find(".fila-relacion .boton-360").ClickAsync(new MouseEventArgs());

        Workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Cliente, cliente.Id, "Refrielectric S.A.", "informacion"));
    }

    /// <summary>
    /// Navegar de /empresas/A a /empresas/B reutiliza el componente (Blazor
    /// solo cambia el parámetro). Nada de A puede sobrevivir: la cabecera, los
    /// trabajadores y, sobre todo, la pestaña Clientes, que se carga una sola
    /// vez por Empresa y quedaría con los Clientes empresariales de A si el
    /// reinicio no la vaciara.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_Empresa_sin_recrear_la_pagina_no_arrastra_nada_de_la_anterior()
    {
        var mediador = Empresa();
        var clienteDeA = new ClienteDeEmpresaDto(Guid.NewGuid(), "Refrielectric S.A.", "A-12345678");
        mediador.Clientes[EmpresaId] = [clienteDeA];
        var otraId = Guid.NewGuid();
        var clienteDeB = new ClienteDeEmpresaDto(Guid.NewGuid(), "Aislamientos Nervión S.L.", "B-87654321");
        mediador.Detalles[otraId] = new EmpresaDetalleDto(
            otraId, "Montajes Ebro S.L.", "B-50.111.222", new DateTime(2024, 1, 10, 9, 0, 0, DateTimeKind.Utc),
            [clienteDeB.Id], Guid.NewGuid(), "4321", "Metal de Zaragoza", false);
        mediador.Cumplimiento[otraId] = 90;
        mediador.Trabajadores[otraId] = [Trabajador(EstadoDocumento.Vigente, "Gil")];
        mediador.Clientes[otraId] = [clienteDeB];
        Registrar(mediador);
        var cut = Renderizar("?pestana=clientes");
        cut.Find(".fila-relacion-nombre").TextContent.Should().Contain("Refrielectric S.A.");

        Navegacion.NavigateTo($"empresas/{otraId}?pestana=clientes");
        cut.Render(p => p.Add(x => x.EmpresaId, otraId));
        cut.WaitForAssertion(() => cut.FindAll("[aria-busy=true]").Should().BeEmpty());

        cut.Find("h1").TextContent.Should().Contain("Montajes Ebro S.L.");
        mediador.Enviadas.OfType<ObtenerClientesDeEmpresaQuery>().Should().Contain(new ObtenerClientesDeEmpresaQuery(otraId));
        cut.FindAll(".fila-relacion-nombre").Select(a => a.TextContent.Trim())
            .Should().Equal(["Aislamientos Nervión S.L."], "la lista es la de B, no la que ya estaba cargada de A");
        cut.Markup.Should().NotContain("Ibertec GmbH").And.NotContain("Refrielectric S.A.");
    }

    /// <summary>
    /// Hallazgo de Codex (#810): EmpresaDetalleDto.ClienteIds no está acotado por
    /// alcance y ObtenerClientesDeEmpresaQuery sí (alcance de gestión, REC-153).
    /// Si la cabecera contara ClienteIds, un actor sin gestión sobre esos
    /// Clientes empresariales vería el tamaño de una cartera que la pestaña no
    /// le enseña. La cabecera y el contador cuentan lo que la lista acotada
    /// devuelve, y una lista vacía no se cuenta (ronda 3): sin alcance de
    /// gestión el handler devuelve vacío a propósito, así que «0 clientes»
    /// afirmaría algo que el actor no puede saber.
    /// </summary>
    [Fact]
    public void El_recuento_de_clientes_sale_de_la_consulta_acotada_y_no_de_ClienteIds()
    {
        var mediador = Empresa();
        mediador.Clientes[EmpresaId] = [];
        Registrar(mediador);

        var cut = Renderizar();

        // Barrera: la entradilla ya tiene los trabajadores; si no, la ausencia sería verde vacío.
        var entradilla = cut.Find(".cabecera-identidad-datos").TextContent;
        entradilla.Should().Contain("25 trabajadores");
        entradilla.Should().NotContain("2 Clientes",
            "EmpresaDetalleDto trae dos ClienteIds, pero ninguno está en el alcance del actor");
        entradilla.Should().NotContain("0 clientes", "una lista vacía puede ser falta de alcance, no una cartera vacía");
        // Sin recuento de clientes, entre el CIF y los trabajadores queda un solo separador.
        entradilla.Count(c => c == '·').Should().Be(1, $"la entradilla es «{entradilla.Trim()}»");
        Pestana(cut, "Clientes").TextContent.Trim().Should().Be("Clientes", "sin recuento que no se pueda afirmar");
    }

    [Fact]
    public async Task La_pestana_Clientes_vacia_no_afirma_que_la_cartera_este_vacia()
    {
        var mediador = Empresa();
        mediador.Clientes[EmpresaId] = [];
        Registrar(mediador);
        var cut = Renderizar();

        await Pestana(cut, "Clientes").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("Sin Clientes que puedas consultar")
            .And.Contain("o no están dentro de tu alcance de gestión");
        cut.Markup.Should().NotContain("todavía no tiene ningún cliente");
    }

    // ── Credencial de acceso ──────────────────────────────────────────────

    private MediatorFalso EmpresaConCredencial(string? contrasena = "clave-secreta-123", string rol = Roles.GestorCae)
    {
        var mediador = Empresa();
        mediador.Credenciales[EmpresaId] = new CredencialAccesoEmpresaDto(
            "https://app.twind.io/login", "Refrielectric S.A.", "usuario.ibertec", contrasena, "Notas internas");
        return Registrar(mediador, rol);
    }

    [Theory]
    [InlineData(Roles.GestorCae)]
    [InlineData(Roles.Consulta)]
    public void La_carpeta_solo_muestra_Abrir_y_conserva_las_credenciales_ocultas(string rol)
    {
        var m = EmpresaConCredencial(rol: rol);
        var cut = Renderizar("?pestana=ficha");
        var caja = cut.Find("[data-caja=acceso]");
        var enlace = caja.QuerySelectorAll("a").Should().ContainSingle().Subject;
        enlace.TextContent.Trim().Should().Be("Abrir");
        enlace.GetAttribute("href").Should().Be("https://app.twind.io/login");
        enlace.GetAttribute("target").Should().Be("_blank");
        enlace.GetAttribute("rel").Should().Be("noopener noreferrer");
        caja.QuerySelectorAll("button").Should().BeEmpty();
        cut.Markup.Should().NotContain("usuario.ibertec").And.NotContain("Refrielectric S.A.").And.NotContain("Notas internas").And.NotContain("clave-secreta-123");
        m.Enviadas.OfType<ObtenerCredencialAccesoEmpresaQuery>().Should().BeEmpty();
        m.Enviadas.OfType<ObtenerCredencialAccesoEmpresaSinContrasenaQuery>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///C:/privado")]
    public void Sin_URL_abrible_no_hay_caja_de_carpeta(string? url)
    {
        var m = EmpresaConCredencial(rol: Roles.Consulta);
        m.Sellos.Clear();
        m.Credenciales[EmpresaId] = m.Credenciales[EmpresaId] with { UrlAcceso = url };
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[data-caja=acceso]").Should().BeEmpty();
        cut.FindAll("[role=tab]").Should().Contain(t => t.TextContent.Trim() == "Ficha");
    }

    [Fact]
    public void Sin_2FA_se_conserva_el_aviso_sin_pintar_la_carpeta_ni_las_credenciales()
    {
        var m = EmpresaConCredencial();
        m.SinDobleFactor = true;
        var cut = Renderizar("?pestana=ficha");
        cut.Find(".acceso-requiere-2fa").TextContent.Should().Contain("Activa la autenticación en dos pasos para ver el acceso.");
        cut.Find(".acceso-requiere-2fa").QuerySelector("a")!.GetAttribute("href").Should().Be(
            "/cuenta/configurar-2fa?motivo=credenciales&returnUrl=" + Uri.EscapeDataString(new Uri(Navegacion.Uri).PathAndQuery),
            "el enlace lleva la vuelta a esta misma ficha (P1-I2)");
        cut.Markup.Should().NotContain("https://app.twind.io/login").And.NotContain("usuario.ibertec");
        Navegacion.Uri.Should().NotContain("configurar-2fa");
        m.Enviadas.OfType<ObtenerCredencialAccesoEmpresaSinContrasenaQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Sin_credencial_no_hay_caja_de_acceso()
    {
        Registrar(Empresa());

        var cut = Renderizar("?pestana=ficha");

        cut.Markup.Should().NotContain("Sin credenciales que puedas consultar.");
        cut.FindAll("[data-caja=acceso]").Should().BeEmpty();
        cut.FindAll(".boton-copiar").Select(b => b.TextContent.Trim()).Should().NotContain("Copiar contraseña");
    }

    // ── Acciones según permiso ────────────────────────────────────────────

    [Fact]
    public async Task Con_escritura_Editar_empresa_abre_el_panel_en_Informacion()
    {
        Registrar(Empresa());
        var cut = Renderizar();

        cut.FindAll("a").Should().Contain(a => a.GetAttribute("href") == $"/empresas/{EmpresaId}/deteccion-trabajadores"
                                               && a.TextContent.Contains("Detectar altas y bajas"));
        cut.Find(".menu-acciones-disparador").Click();
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Editar empresa").ClickAsync(new MouseEventArgs());

        Workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Empresa, EmpresaId, "Ibertec GmbH", "informacion"));
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Editar →");
    }

    [Fact]
    public void Sin_escritura_no_hay_editar_pero_si_detectar()
    {
        Registrar(Empresa(), Roles.Consulta);

        var cut = Renderizar();

        // Barrera: la página cargó entera; si no, la ausencia de abajo sería verde vacío.
        cut.Find("h1").TextContent.Trim().Should().Be("Ibertec GmbH");
        cut.FindAll("a").Should().Contain(a => a.GetAttribute("href") == $"/empresas/{EmpresaId}/deteccion-trabajadores");
        cut.FindAll(".menu-acciones-disparador").Should().BeEmpty();
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Editar →");
    }

    // ── Buscador de la pestaña Trabajadores y ?q= ─────────────────────────

    private static IElement Buscador(IRenderedComponent<EmpresaDetalle> cut) => cut.Find("input[type=search]");

    private static List<string> NombresDeLasFilas(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll(".fila-relacion-nombre").Select(n => n.TextContent.Trim()).ToList();

    [Fact]
    public async Task El_buscador_recorta_la_lista_y_los_chips_lo_deja_en_la_url_y_la_cabecera_sigue_contando_a_todos()
    {
        var mediador = Registrar(Empresa());
        var cut = Renderizar();

        await Buscador(cut).InputAsync("vencido");

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(3));
        Chips(cut).Should().Equal(["Todos · 3", "Sin documentación válida · 3", "Por vencer · 0", "Al día · 0"]);
        Pestana(cut, "Trabajadores").TextContent.Should().Contain("25", "el contador de la pestaña cuenta a todos, no a los buscados");
        new Uri(Navegacion.Uri).Query.Should().Be("?q=vencido");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().Contain(q => q.Busqueda == "vencido" && q.OrdenarPor != null);
    }

    [Fact]
    public void Un_enlace_con_q_y_estado_abre_la_lista_ya_buscada_y_filtrada()
    {
        Registrar(Empresa());

        var cut = Renderizar("?q=uno&estado=por-vencer");

        Buscador(cut).GetAttribute("value").Should().Be("uno");
        ChipPulsado(cut).Should().StartWith("Por vencer");
        EstadosDeLasFilas(cut).Should().Equal(["Por vencer", "Por vencer"], "«Urgente Uno» y «Próximo Uno»; «Vencido Uno» no es de este chip");
        Chips(cut).Should().Equal(["Todos · 3", "Sin documentación válida · 1", "Por vencer · 2", "Al día · 0"]);
        Pestana(cut, "Trabajadores").TextContent.Should().Contain("25");
    }

    [Fact]
    public async Task Sin_coincidencias_Quitar_filtros_limpia_el_texto_y_el_chip_tambien_de_la_url()
    {
        Registrar(Empresa());
        var cut = Renderizar("?q=nadie-se-llama-asi&estado=por-vencer");
        cut.FindAll(".fila-relacion").Should().BeEmpty();
        cut.Find("p.empresa360-vacio-filtrado").TextContent.Should().Contain("Ninguno con ese filtro.");
        Buscador(cut).GetAttribute("value").Should().Be("nadie-se-llama-asi", "sin coincidencias el buscador sigue ahí para corregir el texto");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar filtros").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => Chips(cut)[0].Should().Be("Todos · 25"));
        ChipPulsado(cut).Should().StartWith("Todos");
        Buscador(cut).GetAttribute("value").Should().BeEmpty();
        new Uri(Navegacion.Uri).Query.Should().BeEmpty("si la URL conservara q o estado, recargar los repondría");
    }

    [Fact]
    public async Task Vacio_de_estado_sin_busqueda_tambien_ofrece_Quitar_filtros_y_conserva_otros_parametros()
    {
        Registrar(Empresa([Trabajador(EstadoDocumento.Vigente)]));
        var cut = Renderizar("?estado=por-vencer&extra=conservar");
        cut.FindAll(".fila-relacion").Should().BeEmpty();
        cut.Find("p.empresa360-vacio-filtrado").TextContent.Should().Contain("Ninguno con ese filtro.");
        await cut.Find("p.empresa360-vacio-filtrado button").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion").Should().ContainSingle());
        new Uri(Navegacion.Uri).Query.Should().Be("?extra=conservar");
    }

    [Fact]
    public void Cambiar_q_desde_fuera_vuelve_a_buscar_y_quitarlo_devuelve_la_lista_entera()
    {
        Registrar(Empresa());
        var cut = Renderizar("?q=vencido");
        NombresDeLasFilas(cut).Should().HaveCount(3);

        Navegacion.NavigateTo($"empresas/{EmpresaId}?q=urgente");
        cut.WaitForAssertion(() => EstadosDeLasFilas(cut).Should().Equal(["Por vencer", "Por vencer"]));
        Buscador(cut).GetAttribute("value").Should().Be("urgente");

        Navegacion.NavigateTo($"empresas/{EmpresaId}");
        cut.WaitForAssertion(() => Chips(cut)[0].Should().Be("Todos · 25"));
        Buscador(cut).GetAttribute("value").Should().BeEmpty();
    }

    // ── Nota interna ──────────────────────────────────────────────────────

    private const string NotaGuardada = "Avisar a Leire antes de pedir documentación.";

    private static void ConNota(MediatorFalso mediador, string? notas, bool visible = true) =>
        mediador.Detalles[EmpresaId] = mediador.Detalles[EmpresaId] with { Notas = notas, NotaInternaVisible = visible };

    private static IElement? TarjetaNota(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll("[data-pieza=nota-interna]").SingleOrDefault();

    private static IElement EditorNota(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll("[role=dialog]").Single(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    private static bool EditorNotaAbierto(IRenderedComponent<EmpresaDetalle> cut) =>
        cut.FindAll("[role=dialog]").Any(d => d.QuerySelector("h2")?.TextContent.Trim() == "Nota interna");

    [Fact]
    public void El_equipo_ve_la_nota_interna_en_el_lateral_con_su_pie()
    {
        var mediador = Registrar(Empresa());
        ConNota(mediador, NotaGuardada);

        var cut = Renderizar();

        var tarjeta = TarjetaNota(cut);
        tarjeta.Should().NotBeNull();
        tarjeta!.TextContent.Should().Contain(NotaGuardada).And.Contain("No la ve el Cliente.");
        tarjeta.TextContent.Should().NotContain("Sin nota interna.");
        cut.Find(".cuerpo-con-lateral-lateral").LastElementChild!.GetAttribute("data-pieza").Should().Be("nota-interna");
    }

    [Fact]
    public void Sin_nota_el_equipo_ve_la_tarjeta_con_su_vacio()
    {
        var mediador = Registrar(Empresa());
        ConNota(mediador, null);

        var cut = Renderizar();

        TarjetaNota(cut)!.TextContent.Should().Contain("Sin nota interna.").And.Contain("No la ve el Cliente.");
    }

    /// <summary>
    /// El usuario de Cliente también lee esta ficha. El servidor le responde <c>NotaInternaVisible = false</c> y la página
    /// no pinta la tarjeta, ni siquiera vacía.
    /// </summary>
    [Fact]
    public void Al_usuario_de_Cliente_la_pagina_no_le_pinta_la_tarjeta_ni_vacia()
    {
        var mediador = Registrar(Empresa(), Roles.Cliente);
        ConNota(mediador, null, visible: false);

        var cut = Renderizar();

        TarjetaNota(cut).Should().BeNull();
        cut.Markup.Should().NotContain("Nota interna").And.NotContain("No la ve el Cliente.");
    }

    [Fact]
    public void Consulta_lee_la_nota_pero_no_se_le_ofrece_editarla()
    {
        var mediador = Registrar(Empresa(), Roles.Consulta);
        ConNota(mediador, NotaGuardada);

        var cut = Renderizar();

        TarjetaNota(cut)!.TextContent.Should().Contain(NotaGuardada);
        TarjetaNota(cut)!.QuerySelectorAll("button").Should().BeEmpty();
    }

    [Fact]
    public void Editar_y_guardar_manda_solo_la_nota_con_la_version_leida_y_relee_la_ficha()
    {
        var mediador = Registrar(Empresa());
        ConNota(mediador, NotaGuardada);
        var versionLeida = mediador.Detalles[EmpresaId].Version;
        mediador.DetalleTrasGuardarNota = mediador.Detalles[EmpresaId] with { Notas = "Ahora atiende Jon.", Version = Guid.NewGuid() };
        var cut = Renderizar();

        TarjetaNota(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeTrue());
        var campo = EditorNota(cut).QuerySelector("textarea")!;
        campo.TextContent.Should().Be(NotaGuardada);
        EditorNota(cut).TextContent.Should().Contain("No la ve el Cliente.");
        campo.Input("Ahora atiende Jon.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeFalse());
        mediador.Enviadas.OfType<GuardarNotaInternaEmpresaCommand>().Should().ContainSingle()
            .Which.Should().Be(new GuardarNotaInternaEmpresaCommand(EmpresaId, "Ahora atiende Jon.", versionLeida));
        TarjetaNota(cut)!.TextContent.Should().Contain("Ahora atiende Jon.").And.NotContain(NotaGuardada);
    }

    [Fact]
    public void Un_conflicto_cierra_el_editor_y_relee_la_nota_de_la_otra_persona()
    {
        var mediador = Registrar(Empresa());
        ConNota(mediador, NotaGuardada);
        mediador.ResultadoNota = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona guardó antes."));
        var cut = Renderizar();
        ConNota(mediador, "Lo de la otra persona.");

        TarjetaNota(cut)!.QuerySelectorAll("button").Single(b => b.TextContent.Trim() == "Editar →").Click();
        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeTrue());
        EditorNota(cut).QuerySelector("textarea")!.Input("Lo mío.");
        EditorNota(cut).QuerySelector(".drawer-pie .boton-primario")!.Click();

        cut.WaitForAssertion(() => EditorNotaAbierto(cut).Should().BeFalse());
        TarjetaNota(cut)!.TextContent.Should().Contain("Lo de la otra persona.");
    }
}
