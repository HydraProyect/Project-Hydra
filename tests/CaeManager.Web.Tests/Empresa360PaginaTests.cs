using AngleSharp.Dom;
using Bunit.TestDoubles;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerSelloEmpresa;
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
    }

    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, EmpresaDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, int?> Cumplimiento { get; } = [];
        public Dictionary<Guid, List<TrabajadorListaDto>> Trabajadores { get; } = [];
        public Dictionary<Guid, List<ClienteDeEmpresaDto>> Clientes { get; } = [];
        public Dictionary<Guid, CredencialAccesoEmpresaDto> Credenciales { get; } = [];
        public Dictionary<Guid, SelloEmpresaDto> Sellos { get; } = [];

        public List<object> Enviadas { get; } = [];

        /// <summary>Como AutorizacionSecretosDeTenantBehavior con un rol que lee secretos y sin 2FA (P1-I1).</summary>
        public bool SinDobleFactor { get; set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (SinDobleFactor && request is IConsultaDeSecretosDeTenant or IConsultaDeDatosDeCredencial)
                return Task.FromException<TResponse>(new SegundoFactorRequeridoParaCredencialesException());
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerEmpresaPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerSelloEmpresaQuery q => Sellos.GetValueOrDefault(q.EmpresaId),
            ObtenerCumplimientoEmpresaQuery q => Cumplimiento.GetValueOrDefault(q.EmpresaId),
            ObtenerTrabajadoresQuery q => ResponderTrabajadores(q),
            ObtenerClientesDeEmpresaQuery q => (IReadOnlyList<ClienteDeEmpresaDto>)(Clientes.GetValueOrDefault(q.EmpresaId) ?? []),
            ObtenerCredencialAccesoEmpresaSinContrasenaQuery q => Credenciales.TryGetValue(q.EmpresaId, out var c)
                ? new CredencialAccesoEmpresaSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas)
                : null,
            ObtenerCredencialAccesoEmpresaQuery q => Credenciales.GetValueOrDefault(q.EmpresaId),
            CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.ObtenerCatalogoTrabajadoresEmpresaQuery => CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa.CatalogoTrabajadoresEmpresaDto.Vacio,
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
            if (!string.IsNullOrWhiteSpace(q.EstadoDocumental))
                filas = Enum.TryParse<EstadoDocumento>(q.EstadoDocumental, out var estado)
                    ? filas.Where(t => t.EstadoDocumental == estado)
                    : filas;
            if (q.OrdenarPor == nameof(TrabajadorListaDto.EstadoDocumental))
                filas = filas.OrderBy(t => Rango(t.EstadoDocumental)).ThenBy(t => t.Apellidos).ThenBy(t => t.Nombre).ThenBy(t => t.Id);

            var lista = filas.ToList();
            return new ResultadoPaginado<TrabajadorListaDto>(
                lista.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList(), lista.Count, q.Pagina, q.TamanoPagina);
        }

        private static int Rango(EstadoDocumento? estado) => estado switch
        {
            EstadoDocumento.Vencido => 0,
            EstadoDocumento.Urgente => 1,
            EstadoDocumento.Proximo => 2,
            EstadoDocumento.Vigente => 3,
            _ => 4
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
        entradilla.Should().Contain("CIF B-48.220.917").And.Contain("2 Clientes").And.Contain("25 trabajadores");
        cut.Find("[role=img]").GetAttribute("aria-label").Should().StartWith("72% de cumplimiento");
        // El anillo va a la izquierda de la identidad, no entre las acciones (mockup).
        cabecera.QuerySelector(".cabecera-identidad-anillo [role=img]").Should().NotBeNull();
        cabecera.QuerySelector(".cabecera-identidad-acciones")!.QuerySelector("[role=img]").Should().BeNull();
        // «Detectar altas y bajas» y el menú siguen en la cabecera.
        cabecera.QuerySelector(".cabecera-identidad-acciones")!.TextContent.Should().Contain("Detectar altas y bajas");
        cabecera.QuerySelector(".cabecera-identidad-acciones .menu-acciones-disparador")!.GetAttribute("title").Should().Be("Más acciones");

        mediador.Enviadas.Should().Contain(new ObtenerEmpresaPorIdQuery(EmpresaId));
        mediador.Enviadas.Should().Contain(new ObtenerCumplimientoEmpresaQuery(EmpresaId));
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().OnlyContain(q => q.EmpresaId == EmpresaId,
            "los trabajadores son los de ESTA Empresa, con el alcance que aplica el handler");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().OnlyContain(q => q.TamanoPagina <= 20,
            "nunca se trae la plantilla entera para contar");
    }

    [Fact]
    public void La_informacion_del_lateral_sale_del_detalle()
    {
        Registrar(Empresa());

        var lateral = Renderizar().Find(".cuerpo-con-lateral-lateral").TextContent;

        lateral.Should().Contain("4322").And.Contain("14/03/2025").And.Contain("Metal de Bizkaia").And.Contain("Sí");
    }

    [Fact]
    public void Si_la_empresa_no_llega_se_ofrece_reintentar_y_no_se_consulta_nada_mas()
    {
        Services.ConEnlaceProfundoOtraEmpresa();
        var mediador = Registrar(new MediatorFalso());

        var cut = Renderizar();

        cut.Markup.Should().Contain("No pudimos cargar esta empresa").And.Contain("Puede que ya no exista o que no tengas acceso.");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Reintentar");
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

    [Fact]
    public void El_chip_sin_documentacion_valida_lleva_la_aclaracion_de_lo_que_no_cuenta()
    {
        Registrar(Empresa());

        var cut = Renderizar();

        cut.FindAll(".empresa360-chip")[1].GetAttribute("title")
            .Should().Be("Los documentos obligatorios que faltan no entran en este recuento.");
        cut.FindAll(".empresa360-chip")[2].HasAttribute("title").Should().BeFalse();
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
            .Should().Equal(["Trabajadores", "Clientes", "Documentación", "Agenda", "Ficha", "Historial"]);
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
    public void La_pestana_de_la_url_monta_su_componente_con_la_empresa(string pestana)
    {
        Registrar(Empresa());

        var cut = Renderizar($"?pestana={pestana}");

        Guid propietario = pestana switch
        {
            "documentacion" => cut.FindComponent<Stub<PestanaDocumentacion>>().Instance.Parameters.Get(x => x.PropietarioId),
            "agenda" => cut.FindComponent<Stub<PestanaAgendaContactos>>().Instance.Parameters.Get(x => x.PropietarioId),
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
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("acceso", "firma-documentos", "prevencion", "mutua", "formacion", "sello");
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
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("firma-documentos", "prevencion", "mutua", "formacion", "sello");
        cut.FindComponent<Stub<PestanaSelloEmpresa>>().Instance.Parameters.Get(x => x.EntidadId).Should().Be(EmpresaId);
    }

    [Fact]
    public void Con_escritura_sin_sello_y_con_acceso_la_Ficha_monta_las_dos_cajas()
    {
        var m = EmpresaConCredencial();
        m.Sellos.Clear();
        var cut = Renderizar("?pestana=ficha");
        cut.FindAll("[data-caja]:not([data-caja='catalogo-trabajadores'])").Select(c => c.GetAttribute("data-caja")).Should().Equal("acceso", "firma-documentos", "prevencion", "mutua", "formacion", "sello");
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
        tarjeta!.TextContent.Should().Contain(NotaGuardada).And.Contain("No la ve el cliente.");
        tarjeta.TextContent.Should().NotContain("Sin nota interna.");
        cut.Find(".cuerpo-con-lateral-lateral").LastElementChild!.GetAttribute("data-pieza").Should().Be("nota-interna");
    }

    [Fact]
    public void Sin_nota_el_equipo_ve_la_tarjeta_con_su_vacio()
    {
        var mediador = Registrar(Empresa());
        ConNota(mediador, null);

        var cut = Renderizar();

        TarjetaNota(cut)!.TextContent.Should().Contain("Sin nota interna.").And.Contain("No la ve el cliente.");
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
        cut.Markup.Should().NotContain("Nota interna").And.NotContain("No la ve el cliente.");
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
        EditorNota(cut).TextContent.Should().Contain("No la ve el cliente.");
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
