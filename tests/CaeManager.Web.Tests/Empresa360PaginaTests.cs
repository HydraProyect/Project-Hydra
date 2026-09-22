using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresaSinContrasena;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Empresas.Components;
using CaeManager.Web.Features.Empresas.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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
public class Empresa360PaginaTests : BunitContext
{
    public Empresa360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
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

        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerEmpresaPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerCumplimientoEmpresaQuery q => Cumplimiento.GetValueOrDefault(q.EmpresaId),
            ObtenerTrabajadoresQuery q => ResponderTrabajadores(q),
            ObtenerClientesDeEmpresaQuery q => (IReadOnlyList<ClienteDeEmpresaDto>)(Clientes.GetValueOrDefault(q.EmpresaId) ?? []),
            ObtenerCredencialAccesoEmpresaSinContrasenaQuery q => Credenciales.TryGetValue(q.EmpresaId, out var c)
                ? new CredencialAccesoEmpresaSinContrasenaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Notas)
                : null,
            ObtenerCredencialAccesoEmpresaQuery q => Credenciales.GetValueOrDefault(q.EmpresaId),
            _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
        };

        /// <summary>Misma semántica que ObtenerTrabajadoresQueryHandler (ver la doc de la clase).</summary>
        private ResultadoPaginado<TrabajadorListaDto> ResponderTrabajadores(ObtenerTrabajadoresQuery q)
        {
            IEnumerable<TrabajadorListaDto> filas = q.EmpresaId is { } id ? Trabajadores.GetValueOrDefault(id) ?? [] : [];
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
            [Guid.NewGuid(), Guid.NewGuid()], Guid.NewGuid(), "4322", "Metal de Bizkaia", true);
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

    private static IElement Pestana(IRenderedComponent<EmpresaDetalle> cut, string texto) =>
        cut.FindAll("[role=tab]").Single(b => b.TextContent.Trim().StartsWith(texto, StringComparison.Ordinal));

    // ── Carga y cabecera ──────────────────────────────────────────────────

    [Fact]
    public void La_cabecera_pinta_identidad_recuentos_y_anillo_con_las_consultas_del_panel()
    {
        var mediador = Registrar(Empresa());

        var cut = Renderizar();

        cut.Find(".cabecera-pagina-kicker").TextContent.Trim().Should().Be("Empresa");
        cut.Find("h1").TextContent.Trim().Should().Be("Ibertec GmbH");
        var entradilla = cut.Find(".cabecera-pagina-descripcion").TextContent;
        entradilla.Should().Contain("CIF B-48.220.917").And.Contain("2 clientes").And.Contain("25 trabajadores");
        cut.Find("[role=img]").GetAttribute("aria-label").Should().StartWith("72% de cumplimiento");

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
        var mediador = Registrar(new MediatorFalso());

        var cut = Renderizar();

        cut.Markup.Should().Contain("No pudimos cargar esta empresa").And.Contain("Puede que ya no exista o que no tengas acceso.");
        cut.FindAll("button").Should().Contain(b => b.TextContent.Trim() == "Reintentar");
        mediador.Enviadas.OfType<ObtenerTrabajadoresQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerCredencialAccesoEmpresaSinContrasenaQuery>().Should().BeEmpty();
    }

    // ── Indicadores y chips ───────────────────────────────────────────────

    [Fact]
    public void Los_indicadores_cuentan_trabajadores_por_su_peor_estado()
    {
        Registrar(Empresa());

        var cut = Renderizar();

        cut.FindAll(".empresa360-indicador").Select(b => b.TextContent.Trim()).Should().Equal(
            ["3 trabajadores sin documentación válida", "3 trabajadores con documentos por vencer"]);
        cut.Find(".ventana-contexto-panel").TextContent.Should()
            .Contain("3 con algún documento vencido")
            .And.Contain("Los documentos obligatorios que faltan no entran en este recuento.");
        Chips(cut).Should().Equal(["Todos · 25", "Sin documentación válida · 3", "Por vencer · 3", "Al día · 19"]);
    }

    [Fact]
    public void Un_indicador_en_cero_no_se_pinta()
    {
        Registrar(Empresa([Trabajador(EstadoDocumento.Vigente), Trabajador(EstadoDocumento.Urgente)]));

        var cut = Renderizar();

        cut.FindAll(".empresa360-indicador").Select(b => b.TextContent.Trim()).Should().Equal(
            ["1 trabajador con documentos por vencer"]);
        cut.FindAll(".ventana-contexto").Should().BeEmpty();
    }

    [Fact]
    public async Task Pulsar_un_indicador_filtra_la_pestana_Trabajadores_y_lo_deja_en_la_url()
    {
        Registrar(Empresa());
        var cut = Renderizar("?pestana=clientes");

        await cut.FindAll(".empresa360-indicador")[0].ClickAsync(new MouseEventArgs());

        Pestana(cut, "Trabajadores").GetAttribute("aria-selected").Should().Be("true");
        ChipPulsado(cut).Should().StartWith("Sin documentación válida");
        EstadosDeLasFilas(cut).Should().Equal(["Vencido", "Vencido", "Vencido"]);
        Navegacion.Uri.Should().Contain("estado=sin-documentacion-valida").And.NotContain("pestana=");
    }

    [Fact]
    public void Todos_ordena_del_peor_estado_al_mejor_y_pagina_de_20_en_20()
    {
        Registrar(Empresa());

        var cut = Renderizar();

        var estados = EstadosDeLasFilas(cut);
        estados.Should().HaveCount(20);
        estados.Take(6).Should().Equal(["Vencido", "Vencido", "Vencido", "Urgente", "Urgente", "Próximo"]);
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
    [InlineData("Por vencer", "por-vencer", new[] { "Urgente", "Urgente", "Próximo" })]
    [InlineData("Sin documentación válida", "sin-documentacion-valida", new[] { "Vencido", "Vencido", "Vencido" })]
    public async Task Cada_chip_enseña_solo_su_tramo_y_viaja_en_la_url(string chip, string enUrl, string[] esperados)
    {
        Registrar(Empresa());
        var cut = Renderizar();

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.StartsWith(chip, StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        EstadosDeLasFilas(cut).Should().Equal(esperados);
        Navegacion.Uri.Should().Contain($"estado={enUrl}");
    }

    [Fact]
    public async Task Al_dia_cruza_la_frontera_de_pagina_de_la_lista_ordenada_sin_perder_filas()
    {
        Registrar(Empresa());
        var cut = Renderizar();

        await cut.FindAll(".empresa360-chip").Single(b => b.TextContent.StartsWith("Al día", StringComparison.Ordinal)).ClickAsync(new MouseEventArgs());

        // Tramo [6, 25) de la lista ordenada: 14 filas de su página 1 y 5 de la 2.
        var estados = EstadosDeLasFilas(cut);
        estados.Should().HaveCount(19);
        estados.Should().OnlyContain(e => e == "Vigente" || e == "Sin caducidad");
        estados.Take(12).Should().OnlyContain(e => e == "Vigente", "dentro del tramo también manda el orden");
        cut.FindAll(".paginador").Should().BeEmpty("19 caben en una página");
    }

    [Fact]
    public void El_chip_de_la_url_se_adopta_al_entrar()
    {
        Registrar(Empresa());

        var cut = Renderizar("?estado=por-vencer");

        ChipPulsado(cut).Should().StartWith("Por vencer");
        EstadosDeLasFilas(cut).Should().Equal(["Urgente", "Urgente", "Próximo"]);
    }

    // ── Pestañas ──────────────────────────────────────────────────────────

    [Fact]
    public void Las_pestanas_siguen_el_orden_del_mockup_y_Trabajadores_es_la_de_entrada()
    {
        Registrar(Empresa());

        var cut = Renderizar();

        cut.FindAll("[role=tab]").Select(t => t.ChildNodes.OfType<IText>().First(n => !string.IsNullOrWhiteSpace(n.Data)).Data.Trim())
            .Should().Equal(["Trabajadores", "Clientes", "Documentación", "Agenda", "Sello", "Historial"]);
        Pestana(cut, "Trabajadores").GetAttribute("aria-selected").Should().Be("true");
        Pestana(cut, "Trabajadores").TextContent.Should().Contain("25");
        Pestana(cut, "Clientes").TextContent.Should().Contain("2");
    }

    [Fact]
    public async Task Cambiar_de_pestana_la_deja_en_la_url_y_monta_el_componente_de_la_empresa()
    {
        Registrar(Empresa());
        var cut = Renderizar();

        await Pestana(cut, "Sello").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().Contain("pestana=sello");
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

    // ── Filas: enlace a la página y 360 al panel ──────────────────────────

    [Fact]
    public async Task La_fila_de_trabajador_enlaza_a_su_pagina_y_el_360_abre_su_panel()
    {
        var unico = Trabajador(EstadoDocumento.Vigente, "Ruiz");
        Registrar(Empresa([unico]));
        var cut = Renderizar();

        cut.Find(".fila-relacion-nombre").GetAttribute("href").Should().Be($"/trabajadores/{unico.Id}");
        cut.Find(".fila-relacion-detalle").TextContent.Trim().Should().Be($"DNI {unico.Dni}");

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
        mediador.Enviadas.OfType<ObtenerClientesDeEmpresaQuery>().Should().BeEmpty("se piden al abrir la pestaña, como en el panel");

        await Pestana(cut, "Clientes").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerClientesDeEmpresaQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerClientesDeEmpresaQuery(EmpresaId));
        cut.Find(".fila-relacion-nombre").GetAttribute("href").Should().Be($"/clientes/{cliente.Id}");
        cut.Find(".fila-relacion-detalle").TextContent.Trim().Should().Be("CIF A-12345678");
        Pestana(cut, "Clientes").TextContent.Should().Contain("1", "el contador pasa a ser lo que la lista enseña");

        await cut.Find(".fila-relacion .boton-360").ClickAsync(new MouseEventArgs());

        Workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Cliente, cliente.Id, "Refrielectric S.A.", "informacion"));
    }

    // ── Credencial de acceso ──────────────────────────────────────────────

    private MediatorFalso EmpresaConCredencial(string? contrasena = "clave-secreta-123")
    {
        var mediador = Empresa();
        mediador.Credenciales[EmpresaId] = new CredencialAccesoEmpresaDto(
            "https://app.twind.io/login", "Refrielectric S.A.", "usuario.ibertec", contrasena, "Notas internas");
        return Registrar(mediador);
    }

    [Fact]
    public void La_tarjeta_de_acceso_enseña_lo_no_sensible_y_nunca_la_contrasena()
    {
        var mediador = EmpresaConCredencial();

        var cut = Renderizar();

        var portal = cut.FindAll("a").Single(a => a.TextContent.Contains("Abrir portal"));
        portal.GetAttribute("href").Should().Be("https://app.twind.io/login");
        portal.GetAttribute("target").Should().Be("_blank");
        portal.GetAttribute("rel").Should().Contain("noopener");
        cut.Markup.Should().Contain("Se accede como").And.Contain("usuario.ibertec");
        cut.Markup.Should().NotContain("clave-secreta-123");
        mediador.Enviadas.OfType<ObtenerCredencialAccesoEmpresaSinContrasenaQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerCredencialAccesoEmpresaSinContrasenaQuery(EmpresaId));
        mediador.Enviadas.OfType<ObtenerCredencialAccesoEmpresaQuery>().Should().BeEmpty(
            "la contraseña solo se pide al pulsar «Copiar contraseña» (decisión del propietario 2026-09-21)");
    }

    [Fact]
    public async Task Copiar_contrasena_la_pide_en_el_clic_y_la_manda_al_portapapeles_sin_pintarla()
    {
        var mediador = EmpresaConCredencial();
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar();

        await cut.FindAll(".boton-copiar").Single(b => b.TextContent.Trim() == "Copiar contraseña").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerCredencialAccesoEmpresaQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerCredencialAccesoEmpresaQuery(EmpresaId));
        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal("clave-secreta-123");
        cut.Markup.Should().NotContain("clave-secreta-123", "el valor va al portapapeles, no a la pantalla");
    }

    [Fact]
    public async Task Copiar_contrasena_sin_contrasena_guardada_avisa_y_no_copia()
    {
        EmpresaConCredencial(contrasena: null);
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar();

        await cut.FindAll(".boton-copiar").Single(b => b.TextContent.Trim() == "Copiar contraseña").ClickAsync(new MouseEventArgs());

        modulo.Invocations.Should().BeEmpty("no había nada que copiar");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t => t.Mensaje == "No hay ninguna contraseña guardada.");
    }

    [Fact]
    public void Sin_credencial_la_tarjeta_lo_dice()
    {
        Registrar(Empresa());

        var cut = Renderizar();

        cut.Markup.Should().Contain("Sin credenciales guardadas.");
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
}
