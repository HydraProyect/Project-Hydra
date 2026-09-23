using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cliente 360, página (<see cref="ClienteDetalle"/>) contra su mockup
/// aprobado «Cliente 360 página TALVEG.dc.html»: cabecera sin anillo,
/// indicadores por centro, pestañas con la activa en <c>?pestana=</c>, filas
/// que enlazan a la página 360 de cada entidad y abren su panel con el botón
/// 360, y el error indistinguible de «no existe» / «fuera de alcance».
/// Prueban efectos (qué se ve, qué consultas salen, qué panel queda abierto,
/// a qué URL se va); bUnit no evalúa CSS.
///
/// <para>
/// Solo se pintan las pestañas propias (Centros, Empresas, Subcontratas): las
/// compartidas (Blindaje 42.1, Documentación, Agenda, Historial) son los
/// mismos componentes del panel, con sus propios tests.
/// </para>
/// </summary>
public class Cliente360PaginaTests : BunitContext
{
    /// <summary>Tope de centros que pinta la página (ClienteDetalle.MaximoCentros, interno).</summary>
    private const int MaximoCentros = 200;

    private static readonly DateTime Alta = new(2019, 3, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>BotonCopiar importa ./js/clipboard.js; Pestanas enfoca por interop.</summary>
    public Cliente360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private sealed class MediatorFalso : IMediator
    {
        public Dictionary<Guid, ClienteDetalleDto> Detalles { get; } = [];
        public Dictionary<Guid, ResumenClienteDto> Resumenes { get; } = [];
        public List<CentroListaDto> Centros { get; } = [];

        /// <summary>Total que declara la consulta; null = los que devuelve.</summary>
        public int? TotalCentros { get; set; }

        public List<EmpresaDeClienteDto> Empresas { get; } = [];

        /// <summary>Respuestas por Cliente empresarial; sin entrada, las de <see cref="Empresas"/> y <see cref="Centros"/>.</summary>
        public Dictionary<Guid, List<EmpresaDeClienteDto>> EmpresasPorCliente { get; } = [];
        public Dictionary<Guid, List<CentroListaDto>> CentrosPorCliente { get; } = [];
        public List<SubcontrataDeClienteDto> Subcontratas { get; } = [];
        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)Responder(request)!);
        }

        private object? Responder(object request) => request switch
        {
            ObtenerClientePorIdQuery q => Detalles.GetValueOrDefault(q.Id),
            ObtenerResumenClienteQuery q => Resumenes.GetValueOrDefault(q.ClienteId),
            ObtenerCentrosQuery q when q.ClienteId is { } c && CentrosPorCliente.TryGetValue(c, out var propios) =>
                new ResultadoPaginado<CentroListaDto>(propios, propios.Count, 1, MaximoCentros),
            ObtenerCentrosQuery => new ResultadoPaginado<CentroListaDto>(
                Centros, TotalCentros ?? Centros.Count, 1, MaximoCentros),
            ObtenerEmpresasDeClienteQuery q when EmpresasPorCliente.TryGetValue(q.ClienteId, out var propias) => propias,
            ObtenerEmpresasDeClienteQuery => Empresas,
            ObtenerSubcontratasDeClienteQuery => Subcontratas,
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
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

    private MediatorFalso Registrar(MediatorFalso mediador, string rol = Roles.GestorCae)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddLocalization();
        this.ConRolDeEscritura(rol);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        return mediador;
    }

    private IRenderedComponent<ClienteDetalle> Renderizar(Guid id, string? consulta = null)
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo($"clientes/{id}{consulta}");
        var cut = Render<ClienteDetalle>(p => p.Add(x => x.ClienteId, id));
        cut.WaitForAssertion(() => cut.FindAll(".cabecera-pagina, .estado-vacio").Should().NotBeEmpty());
        return cut;
    }

    /// <summary>Un cliente «Refrielectric S.A.» con 3 centros y 42 trabajadores, sin centros ni relaciones.</summary>
    private static (Guid Id, MediatorFalso Mediador) ClienteBase(bool critico = false, string? notas = null)
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso();
        mediador.Detalles[id] = new ClienteDetalleDto(id, "Refrielectric S.A.", "A-48.220.917", critico, notas, Alta, null, Guid.NewGuid());
        mediador.Resumenes[id] = new ResumenClienteDto(id, "Refrielectric S.A.", "A-48.220.917", critico, Alta, null, 3, 42);
        return (id, mediador);
    }

    private static IncidenciaCentroDto Incidencia(EstadoDocumento estado) =>
        new("Seguro RC", AmbitoCausa.Empresa, estado, null, null, null);

    private static CentroListaDto Centro(
        string nombre, EstadoCentro estado, int vencidas = 0, int proximas = 0, int? cumplimiento = 80,
        string empresa = "Ibertec GmbH") =>
        new(Guid.NewGuid(), nombre, null, Guid.NewGuid(), "Refrielectric S.A.", Guid.NewGuid(), empresa,
            estado, cumplimiento,
            new RecuentosCentroDto(
                Enumerable.Range(0, vencidas).Select(_ => Incidencia(EstadoDocumento.Vencido)).ToList(),
                Enumerable.Range(0, proximas).Select(_ => Incidencia(EstadoDocumento.Proximo)).ToList()));

    private static List<string> Textos(IEnumerable<IElement> elementos) =>
        elementos.Select(e => e.TextContent.Trim()).ToList();

    // ── Cabecera ──────────────────────────────────────────────────────────

    [Fact]
    public void La_cabecera_dice_Cliente_razon_social_CIF_y_recuentos_sin_anillo()
    {
        var (id, mediador) = ClienteBase(critico: true);
        Registrar(mediador);

        var cut = Renderizar(id);

        var cabecera = cut.Find(".cabecera-pagina");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Cliente",
            "en pantalla el Cliente empresarial se rotula «Cliente» (contrato Gen2 § 14)");
        cabecera.QuerySelector(".cliente360-meta")!.TextContent.Should()
            .Contain("CIF A-48.220.917").And.Contain("3 centros · 42 trabajadores");
        cabecera.QuerySelector(".cliente360-meta button")
            .Should().NotBeNull("el CIF se copia con BotonCopiar");
        Textos(cabecera.QuerySelectorAll(".cliente360-indicadores .badge")).Should().Equal(["Crítico"]);
        cut.FindAll(".anillo-cumplimiento, [class*='anillo']").Should().BeEmpty(
            "no existe porcentaje de cumplimiento por Cliente empresarial: un anillo inventaría uno");
    }

    [Fact]
    public void Un_cliente_no_critico_no_pinta_el_badge()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.FindAll(".cliente360-indicadores").Should().BeEmpty("sin crítico ni indicadores no queda nada que pintar");
    }

    // ── Indicadores por centro ────────────────────────────────────────────

    [Fact]
    public void Los_indicadores_cuentan_centros_y_su_ventana_los_nombra()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([
            Centro("Planta Barakaldo", EstadoCentro.Bloqueado, vencidas: 2, empresa: "Montajes Ebro S.L."),
            Centro("Almacén Getafe", EstadoCentro.Vencido, vencidas: 1, proximas: 1),
            Centro("Oficinas Bilbao", EstadoCentro.Vigente)]);
        Registrar(mediador);

        var cut = Renderizar(id);

        var indicadores = cut.FindAll(".cliente360-indicador-boton");
        indicadores.Select(i => i.GetAttribute("data-indicador")).Should().Equal(["bloqueados", "vencidos", "proximos"]);
        Textos(indicadores).Should().Equal([
            "Acceso bloqueado en 1 de 3 centros", "2 centros con vencidos", "1 centro con próximos"]);

        var ventanas = cut.FindAll(".cliente360-indicadores .ventana-contexto");
        ventanas.Should().HaveCount(3);
        ventanas[0].QuerySelector(".ventana-contexto-titulo")!.TextContent.Should().Be("1 centro con acceso bloqueado");
        Textos(ventanas[0].QuerySelectorAll(".ventana-linea")).Should().Equal(["Planta Barakaldo · Montajes Ebro S.L."]);
        Textos(ventanas[1].QuerySelectorAll(".ventana-linea")).Should().Equal([
            "Planta Barakaldo · 2 vencidos", "Almacén Getafe · 1 vencido"]);
        Textos(ventanas[2].QuerySelectorAll(".ventana-linea")).Should().Equal(["Almacén Getafe · 1 próximo"]);
        ventanas[0].GetAttribute("aria-label").Should().Be(
            "Acceso bloqueado en 1 de 3 centros: Planta Barakaldo · Montajes Ebro S.L.. Pulsa para ver los centros");
    }

    [Fact]
    public void Un_indicador_en_cero_no_se_pinta()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([
            Centro("Almacén Getafe", EstadoCentro.Proximo, proximas: 2),
            Centro("Oficinas Bilbao", EstadoCentro.Vigente)]);
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.FindAll(".cliente360-indicador-boton").Select(i => i.GetAttribute("data-indicador"))
            .Should().Equal(["proximos"], "sin bloqueados ni vencidos solo queda el de próximos");
    }

    [Fact]
    public void Pulsar_un_indicador_lleva_a_la_pestana_Centros()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.Add(Centro("Planta Barakaldo", EstadoCentro.Bloqueado));
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", "B-12345678"));
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=empresas");
        cut.Find(".pestanas-boton-activa").TextContent.Should().Contain("Empresas");

        cut.Find(".cliente360-indicador-boton").Click();

        cut.WaitForAssertion(() => cut.Find(".pestanas-boton-activa").TextContent.Should().Contain("Centros"));
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query.Should().NotContain("pestana",
            "Centros es la pestaña por defecto: no deja parámetro en la URL");
    }

    /// <summary>
    /// Navegar de /clientes/A a /clientes/B reutiliza el componente (Blazor
    /// solo cambia el parámetro): cabecera, centros y relaciones deben ser los
    /// de B, sin restos de A.
    /// </summary>
    [Fact]
    public void Cambiar_de_Cliente_sin_recrear_la_pagina_no_arrastra_nada_del_anterior()
    {
        var (idA, mediador) = ClienteBase();
        mediador.CentrosPorCliente[idA] = [Centro("Planta Barakaldo", EstadoCentro.Bloqueado)];
        mediador.EmpresasPorCliente[idA] = [new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", "B-12345678")];
        var idB = Guid.NewGuid();
        mediador.Detalles[idB] = new ClienteDetalleDto(idB, "Aislamientos Nervión S.L.", "B-99.000.111", false, null, Alta, null, Guid.NewGuid());
        mediador.Resumenes[idB] = new ResumenClienteDto(idB, "Aislamientos Nervión S.L.", "B-99.000.111", false, Alta, null, 1, 7);
        mediador.CentrosPorCliente[idB] = [Centro("Nave logística Tudela", EstadoCentro.Vigente, empresa: "Montajes Ebro S.L.")];
        mediador.EmpresasPorCliente[idB] = [new EmpresaDeClienteDto(Guid.NewGuid(), "Montajes Ebro S.L.", "B-50111222")];
        Registrar(mediador);
        var cut = Renderizar(idA, "?pestana=empresas");
        cut.WaitForAssertion(() => cut.Find(".fila-relacion-nombre").TextContent.Should().Contain("Ibertec GmbH"));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"clientes/{idB}?pestana=empresas");
        cut.Render(p => p.Add(x => x.ClienteId, idB));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Contain("Aislamientos Nervión S.L."));
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion-nombre").Select(a => a.TextContent.Trim())
            .Should().Equal(["Montajes Ebro S.L."]));
        mediador.Enviadas.OfType<ObtenerEmpresasDeClienteQuery>().Should().Contain(new ObtenerEmpresasDeClienteQuery(idB));
        cut.Markup.Should().NotContain("Refrielectric S.A.").And.NotContain("Ibertec GmbH").And.NotContain("Planta Barakaldo");
    }

    [Fact]
    public void Los_centros_salen_de_ObtenerCentros_filtrada_por_el_cliente_del_peor_estado_al_mejor()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);

        Renderizar(id);

        var consulta = mediador.Enviadas.OfType<ObtenerCentrosQuery>().Single();
        consulta.ClienteId.Should().Be(id);
        consulta.OrdenarPor.Should().Be(nameof(CentroListaDto.Estado));
        consulta.Descendente.Should().BeTrue("EstadoCentro va de mejor a peor: descendente pone Bloqueado primero");
        consulta.Pagina.Should().Be(1);
        consulta.TamanoPagina.Should().Be(MaximoCentros);
        consulta.Busqueda.Should().BeNull();
        consulta.Estado.Should().BeNull();
    }

    // ── Pestañas ──────────────────────────────────────────────────────────

    [Fact]
    public void Las_pestanas_van_en_el_orden_del_mockup_con_recuentos_y_Centros_por_defecto()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([Centro("Planta Barakaldo", EstadoCentro.Vigente), Centro("Almacén Getafe", EstadoCentro.Vigente)]);
        mediador.TotalCentros = 2;
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", null));
        Registrar(mediador);

        var cut = Renderizar(id);

        Textos(cut.FindAll("[role=tab]")).Should().Equal([
            "Centros2 centros", "Empresas1 empresa", "Subcontratas0 subcontratas",
            "Blindaje 42.1", "Documentación", "Agenda", "Historial"]);
        cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Centros");
    }

    [Fact]
    public void La_pestana_activa_se_lee_de_la_URL_y_se_escribe_en_ella()
    {
        var (id, mediador) = ClienteBase();
        mediador.Subcontratas.Add(new SubcontrataDeClienteDto(Guid.NewGuid(), "Andamios Cantábrico S.L."));
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=subcontratas");
        cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Subcontratas");

        cut.FindAll("[role=tab]").Single(t => t.TextContent.StartsWith("Empresas")).Click();

        cut.WaitForAssertion(() => cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Empresas"));
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query.Should().Be("?pestana=empresas");
    }

    [Fact]
    public void Una_pestana_desconocida_en_la_URL_cae_en_Centros()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);

        var cut = Renderizar(id, "?pestana=inventada");

        cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Centros");
    }

    // ── Filas ─────────────────────────────────────────────────────────────

    [Fact]
    public void Cada_centro_enlaza_a_su_pagina_y_su_boton_360_abre_su_panel()
    {
        var (id, mediador) = ClienteBase();
        var bloqueado = Centro("Planta Barakaldo", EstadoCentro.Bloqueado, vencidas: 3, proximas: 1, cumplimiento: 40,
            empresa: "Montajes Ebro S.L.");
        var vigente = Centro("Oficinas Bilbao", EstadoCentro.Vigente, cumplimiento: null);
        mediador.Centros.AddRange([bloqueado, vigente]);
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.Find(".cliente360-resumen-lista").TextContent.Should().Be("2 centros de este cliente, del peor estado al mejor.");
        var filas = cut.FindAll("li.fila-relacion");
        filas.Select(f => f.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href"))
            .Should().Equal([$"/centros/{bloqueado.Id}", $"/centros/{vigente.Id}"], "el orden es el de la consulta: peor primero");
        filas[0].QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("Empresa: Montajes Ebro S.L. · 3 vencidos · 1 próximo");
        filas[1].QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("Empresa: Ibertec GmbH");
        filas[0].QuerySelector(".cliente360-cumplimiento")!.TextContent.Trim().Should().Be("40 %");
        filas[1].QuerySelector(".cliente360-cumplimiento")!.TextContent.Trim().Should().Be("Sin requisitos");
        filas[0].QuerySelector(".badge")!.TextContent.Trim().Should().Be(Features.Centros.EstadoCentroUi.Texto(EstadoCentro.Bloqueado));

        filas[0].QuerySelector("button.boton-360")!.Click();

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(
            new WorkspaceFrame(EntidadWorkspace.Centro, bloqueado.Id, "Planta Barakaldo", "informacion"));
    }

    [Fact]
    public void Con_mas_centros_de_los_que_se_pintan_la_lista_lo_dice()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([Centro("Planta Barakaldo", EstadoCentro.Bloqueado), Centro("Almacén Getafe", EstadoCentro.Vencido)]);
        mediador.TotalCentros = 250;
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.Find(".cliente360-resumen-lista").TextContent.Should().Be("Se muestran los 2 centros con peor estado de 250.");
        cut.Find(".cliente360-indicador-boton").TextContent.Trim().Should().Be("Acceso bloqueado en 1 de 250 centros");
    }

    [Fact]
    public void Cada_empresa_enlaza_a_su_pagina_con_su_CIF_y_su_boton_360_abre_su_panel()
    {
        var (id, mediador) = ClienteBase();
        var empresa = new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", "B-12345678");
        mediador.Empresas.Add(empresa);
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=empresas");

        var fila = cut.Find("li.fila-relacion");
        fila.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href").Should().Be($"/empresas/{empresa.Id}");
        fila.QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("B-12345678");

        fila.QuerySelector("button.boton-360")!.Click();

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(
            new WorkspaceFrame(EntidadWorkspace.Empresa, empresa.Id, "Ibertec GmbH", "informacion"));
    }

    [Fact]
    public void El_nombre_de_una_subcontrata_abre_su_panel_porque_no_tiene_pagina()
    {
        var (id, mediador) = ClienteBase();
        var subcontrata = new SubcontrataDeClienteDto(Guid.NewGuid(), "Andamios Cantábrico S.L.");
        mediador.Subcontratas.Add(subcontrata);
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=subcontratas");

        cut.FindAll("li.fila-relacion a").Should().BeEmpty("Subcontrata 360 no tiene página: un enlace daría 404");

        cut.Find("button.fila-relacion-nombre").Click();

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(
            new WorkspaceFrame(EntidadWorkspace.Subcontrata, subcontrata.Id, "Andamios Cantábrico S.L.", "informacion"));
    }

    // ── Error ─────────────────────────────────────────────────────────────

    /// <summary>
    /// ObtenerClientePorIdQuery devuelve null tanto si el cliente no existe
    /// como si queda fuera del alcance (IAlcanceDatosService + RLS): la página
    /// no distingue, no filtra la existencia, y no pide nada más.
    /// </summary>
    [Fact]
    public void Un_cliente_inexistente_o_fuera_de_alcance_muestra_el_error_con_Reintentar()
    {
        var mediador = Registrar(new MediatorFalso());
        var id = Guid.NewGuid();

        var cut = Renderizar(id);

        cut.Find(".estado-vacio").TextContent.Should()
            .Contain("No pudimos cargar este cliente").And.Contain("Puede que ya no exista o que no tengas acceso.");
        cut.FindAll(".cabecera-pagina").Should().BeEmpty();
        mediador.Enviadas.Should().ContainSingle().Which.Should().BeOfType<ObtenerClientePorIdQuery>();

        mediador.Detalles[id] = new ClienteDetalleDto(id, "Refrielectric S.A.", "A-48.220.917", false, null, Alta, null, Guid.NewGuid());
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").Click();

        cut.WaitForAssertion(() => cut.Find(".cabecera-pagina h1").TextContent.Trim().Should().Be("Refrielectric S.A."));
    }

    // ── Acciones y lateral ────────────────────────────────────────────────

    [Fact]
    public async Task Con_escritura_Editar_cliente_y_los_Editar_del_lateral_abren_el_panel()
    {
        var (id, mediador) = ClienteBase(notas: "Llamar antes de ir.");
        Registrar(mediador);
        var cut = Renderizar(id);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        cut.Find(".cliente360-nota").TextContent.Should().Be("Llamar antes de ir.");
        cut.Find(".cliente360-nota-pie").TextContent.Should().Be("Solo visible para tu equipo.");

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Editar cliente").ClickAsync(new MouseEventArgs());
        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Cliente, id, "Refrielectric S.A.", "informacion"));

        var editar = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Editar →").ToList();
        editar.Should().HaveCount(2, "Información y Nota interna");
        await editar[1].ClickAsync(new MouseEventArgs());
        workspace.FrameActual!.PestanaActiva.Should().Be("notas");
    }

    [Fact]
    public void Sin_escritura_no_hay_menu_ni_Editar()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador, Roles.Consulta);

        var cut = Renderizar(id);

        cut.FindAll(".menu-acciones").Should().BeEmpty("con su único elemento oculto quedaría un «⋯» vacío");
        cut.FindAll("button").Where(b => b.TextContent.Trim() == "Editar →").Should().BeEmpty();
        cut.Find(".cliente360-nota").TextContent.Should().Be("Sin nota interna.");
    }
}
