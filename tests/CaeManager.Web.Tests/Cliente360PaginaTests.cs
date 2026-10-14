using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Blindaje42.Commands.SolicitarCertificacionTgss;
using CaeManager.Application.Blindaje42.Queries.ObtenerBlindajeEmpresasDeCliente;
using CaeManager.Application.Blindaje42.Queries.ObtenerHistorialCertificacionesTgss;
using CaeManager.Web.Features.Blindaje42.Components;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Common;
using CaeManager.Application.TiposDocumento.Commands.EstablecerToleranciaClienteEmpresarial;
using CaeManager.Application.TiposDocumento.Queries.ObtenerToleranciasClienteEmpresarial;
using CaeManager.Domain.Common;
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
/// Cliente 360, página (<see cref="ClienteDetalle"/>): tarjeta de identidad compartida con fracción de Centros,
/// indicadores por centro, pestañas con la activa en <c>?pestana=</c>, filas
/// que enlazan a la página 360 de cada entidad y abren su panel con el botón
/// 360, y el error indistinguible de «no existe» / «fuera de alcance».
/// Prueban efectos (qué se ve, qué consultas salen, qué panel queda abierto,
/// a qué URL se va); bUnit no evalúa CSS.
///
/// <para>
/// Centros, Empresas y Subcontratas conservan sus listas. Documentación
/// compone la certificación TGSS y los documentos con los mismos componentes
/// del panel; sus acciones conservan los permisos de escritura.
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
        public List<BlindajeEmpresaDto> Certificaciones { get; } = [];
        public List<SolicitudCertificacionTgssDto> HistorialCertificaciones { get; } = [];

        /// <summary>Los bloqueos de acceso visibles (la consulta sin Centro): la página se queda con los de su Cliente empresarial.</summary>
        public List<DocumentacionBloqueantePendienteDto> Bloqueos { get; } = [];
        public bool FallaLecturaBloqueos { get; set; }
        public List<object> Enviadas { get; } = [];

        /// <summary>Lo que ocurre en el servidor al guardar una tolerancia (p. ej. que los bloqueos cambien).</summary>
        public Action? AlGuardarTolerancia { get; set; }

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
            ObtenerBlindajeEmpresasDeClienteQuery => Certificaciones,
            ObtenerHistorialCertificacionesTgssQuery => HistorialCertificaciones,
            SolicitarCertificacionTgssCommand => Result.Exito(Guid.NewGuid()),
            ObtenerDocumentacionBloqueantePendienteQuery => FallaLecturaBloqueos
                ? throw new InvalidOperationException("Fallo de lectura de bloqueos")
                : (IReadOnlyList<DocumentacionBloqueantePendienteDto>)Bloqueos.ToList(),
            ObtenerToleranciasClienteEmpresarialQuery => (IReadOnlyList<ToleranciaTipoDocumentoDto>)
                [new ToleranciaTipoDocumentoDto(Guid.NewGuid(), "Formación PRL", AmbitoAplicacion.Trabajador, 0)],
            EstablecerToleranciaClienteEmpresarialCommand => GuardarTolerancia(),
            _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
        };

        private Result GuardarTolerancia()
        {
            AlGuardarTolerancia?.Invoke();
            return Result.Exito();
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

    private MediatorFalso Registrar(MediatorFalso mediador, string rol = Roles.GestorCae)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton(TimeProvider.System);
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
        cut.WaitForAssertion(() => cut.FindAll("h1, .estado-vacio").Should().NotBeEmpty());
        return cut;
    }

    /// <summary>Un cliente «Refrielectric S.A.» con 3 centros y 42 trabajadores, sin centros ni relaciones.</summary>
    private static (Guid Id, MediatorFalso Mediador) ClienteBase(bool critico = false, string? notas = null)
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso();
        mediador.Detalles[id] = new ClienteDetalleDto(id, "Refrielectric S.A.", "A-48.220.917", critico, notas, Alta, null, Guid.NewGuid(), NotaInternaVisible: true);
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

    private static DocumentacionBloqueantePendienteDto Bloqueo(Guid clienteEmpresarialId, string centro, Guid trabajadorId, string trabajador) =>
        new(Guid.NewGuid(), centro, trabajadorId, trabajador, Guid.NewGuid(), "Formación PRL", ClienteId: clienteEmpresarialId);

    private static List<string> Textos(IEnumerable<IElement> elementos) =>
        elementos.Select(e => e.TextContent.Trim()).ToList();

    [Theory]
    [InlineData(1, "1 bloqueado por la plataforma")]
    [InlineData(2, "2 bloqueados por la plataforma")]
    public void El_detalle_cuenta_el_bloqueo_de_plataforma_sin_estado_documental(int cantidad, string esperado)
    {
        var (id, mediador) = ClienteBase();
        var centro = Centro("Centro solo plataforma", EstadoCentro.Bloqueado) with
        {
            Recuentos = new RecuentosCentroDto(
                Enumerable.Range(0, cantidad).Select(_ => new IncidenciaCentroDto("Acceso denegado", AmbitoCausa.Empresa, null, null, null, null)).ToList(), [])
        };
        mediador.Centros.Add(centro);
        Registrar(mediador);
        var cut = Renderizar(id);
        cut.Find(".fila-relacion-detalle").TextContent.Should().Be(esperado);
        cut.Find(".fila-relacion-detalle").TextContent.Should().NotContain("vencido").And.NotContain("pendiente");
    }

    [Fact]
    public void La_franja_conserva_el_estado_seleccionado_sin_coincidencias()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.Add(Centro("Centro vigente", EstadoCentro.Vigente));
        Registrar(mediador);
        var cut = Renderizar(id, "?estado=Bloqueado&q=Centro&extra=conservar");
        var filtro = cut.Find(".franja-estado [data-estado=Bloqueado]");
        filtro.GetAttribute("aria-pressed").Should().Be("true");
        filtro.QuerySelector(".franja-estado-recuento")!.TextContent.Should().Be("0");
        cut.FindAll(".fila-relacion").Should().BeEmpty();
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Centro").And.Contain("extra=conservar");
    }

    // ── Cabecera ──────────────────────────────────────────────────────────

    [Fact]
    public void La_cabecera_en_tarjeta_pinta_identidad_CIF_y_recuentos_sin_anillo_si_no_hay_centros()
    {
        var (id, mediador) = ClienteBase(critico: true);
        Registrar(mediador);

        var cut = Renderizar(id);

        var cabecera = cut.Find("[data-pieza=cabecera-identidad]");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Refrielectric S.A.");
        cabecera.QuerySelector(".cabecera-identidad-kicker")!.TextContent.Trim().Should().Be("Cliente",
            "en pantalla el Cliente se rotula «Cliente» (CONTRATO_TERMINOLOGIA § 3.2)");
        cabecera.QuerySelector(".cabecera-identidad-datos")!.TextContent.Should()
            .Contain("CIF A-48.220.917").And.Contain("3 centros · 42 trabajadores");
        cabecera.QuerySelector(".cabecera-identidad-datos button")
            .Should().NotBeNull("el CIF se copia con BotonCopiar");
        Textos(cabecera.QuerySelectorAll(".cabecera-identidad-titular .badge")).Should().Equal(["Crítico"]);
        cut.FindAll(".anillo-cumplimiento, [class*='anillo']").Should().BeEmpty(
            "sin Centros no hay un universo de gestión CAE que medir");
    }

    /// <summary>
    /// Hallazgo de Codex (#810, ronda 2): el alta permite un Cliente empresarial
    /// sin CIF, y la Empresa de una relación también puede no tenerlo. Ni «CIF»
    /// con el valor vacío ni un botón de copiar sin nada que copiar; en la fila
    /// de relación, «—», como en Empresa 360.
    /// </summary>
    [Fact]
    public void Sin_CIF_no_se_pinta_la_etiqueta_ni_el_boton_de_copiar()
    {
        var (id, mediador) = ClienteBase();
        // ClienteDetalleDto.Cif es string, pero el handler proyecta Empresa.Cif (string?)
        // con «!»: en ejecución llega null. Se reproduce ese valor.
        mediador.Detalles[id] = mediador.Detalles[id] with { Cif = null! };
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", null));
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Otra contratista", null));
        Registrar(mediador);

        var cut = Renderizar(id, "?pestana=empresas");

        var meta = cut.Find(".cabecera-identidad-datos");
        // Barrera: la entradilla ya tiene los recuentos; si no, la ausencia sería verde vacío.
        meta.TextContent.Trim().Should().StartWith("3 centros · 42 trabajadores");
        meta.TextContent.Should().NotContain("CIF");
        meta.QuerySelector("button").Should().BeNull("no hay CIF que copiar");
        cut.Find(".fila-relacion-detalle").TextContent.Trim().Should().Be("—");
    }

    [Fact]
    public void Un_cliente_no_critico_no_pinta_el_badge()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.FindAll(".cabecera-identidad-titular .badge").Should().BeEmpty("solo un Cliente empresarial crítico lleva la marca");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void La_cabecera_nombra_la_Empresa_contratista_solo_si_hay_una_visible(int cantidad)
    {
        var (id, mediador) = ClienteBase();
        var nombres = new[] { "Ibertec GmbH", "Montajes Ebro S.L." };
        mediador.Empresas.AddRange(nombres.Take(cantidad)
            .Select(nombre => new EmpresaDeClienteDto(Guid.NewGuid(), nombre, null)));
        Registrar(mediador);

        var cut = Renderizar(id);

        // La carga de Empresas debe haber terminado: una ausencia durante la carga no prueba el caso de varias.
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerSubcontratasDeClienteQuery>().Should().ContainSingle());
        var datos = cut.Find(".cabecera-identidad-datos").TextContent;
        if (cantidad == 1)
            datos.Should().Contain("Entra como contratista: Ibertec GmbH");
        else
            datos.Should().NotContain(nombres[0]).And.NotContain(nombres[1]);
    }

    [Fact]
    public void La_tarjeta_de_cabecera_conserva_las_ranuras_y_un_solo_titulo()
    {
        var (id, mediador) = ClienteBase(critico: true);
        Registrar(mediador);

        var cut = Renderizar(id);

        var cabecera = cut.Find("header.cabecera-identidad[data-pieza=cabecera-identidad]");
        cabecera.ClassList.Should().Contain("velo-cumplimiento");
        cut.FindAll(".cabecera-pagina").Should().BeEmpty();
        cut.FindAll("h1").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Refrielectric S.A.");
        cabecera.QuerySelector(".cabecera-identidad-titular h1 + .badge")!.TextContent.Should().Be("Crítico");
        cabecera.QuerySelector(".cabecera-identidad-datos")!.TextContent.Should()
            .Contain("CIF A-48.220.917").And.Contain("3 centros · 42 trabajadores");
        cabecera.QuerySelector(".cabecera-identidad-acciones .menu-acciones-disparador")!
            .GetAttribute("aria-label").Should().Be("Más acciones");
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
            "Bloqueo de la plataforma CAE en 1 de 3 centros", "2 centros con vencidos", "1 centro con próximos"]);

        var ventanas = cut.FindAll(".cliente360-indicadores .ventana-contexto");
        ventanas.Should().HaveCount(3);
        ventanas[0].QuerySelector(".ventana-contexto-titulo")!.TextContent.Should().Be("1 centro con bloqueo de la plataforma CAE");
        Textos(ventanas[0].QuerySelectorAll(".ventana-linea")).Should().Equal(["Planta Barakaldo"],
            "el Centro es del Cliente: con una sola Empresa contratista a la vista no se nombra ninguna");
        Textos(ventanas[1].QuerySelectorAll(".ventana-linea")).Should().Equal([
            "Planta Barakaldo · 2 vencidos", "Almacén Getafe · 1 vencido"]);
        Textos(ventanas[2].QuerySelectorAll(".ventana-linea")).Should().Equal(["Almacén Getafe · 1 próximo"]);
        ventanas[0].GetAttribute("aria-label").Should().Be(
            "Bloqueo de la plataforma CAE en 1 de 3 centros: Planta Barakaldo. Pulsa para ver los centros");
    }

    /// <summary>
    /// «Bloqueado» es un estado del Trabajador (2026-10-03): el Cliente empresarial cuenta Trabajadores bloqueados en SUS Centros
    /// (los bloqueos de otro Cliente empresarial no cuentan; un Trabajador bloqueado en dos Centros cuenta una vez) y los nombra
    /// con su Centro. Los Centros con bloqueo de la plataforma CAE (D-7) son un indicador aparte.
    /// </summary>
    [Fact]
    public void Los_Trabajadores_bloqueados_de_sus_centros_son_un_indicador_propio_y_no_el_de_los_centros_bloqueados()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.Add(Centro("Almacén Getafe", EstadoCentro.Faltante, vencidas: 1));
        var juan = Guid.NewGuid();
        mediador.Bloqueos.AddRange([
            Bloqueo(id, "Almacén Getafe", juan, "Juan Pérez"),
            Bloqueo(id, "Oficinas Bilbao", juan, "Juan Pérez"),
            Bloqueo(id, "Almacén Getafe", Guid.NewGuid(), "Marco Vila"),
            Bloqueo(Guid.NewGuid(), "Planta de otro Cliente", Guid.NewGuid(), "Ajeno Ajeno")]);
        Registrar(mediador);

        var cut = Renderizar(id);

        var indicadores = cut.FindAll(".cliente360-indicador-boton");
        indicadores.Select(i => i.GetAttribute("data-indicador")).Should().Equal(["trabajadores-bloqueados", "vencidos"]);
        Textos(indicadores).Should().Equal(["2 trabajadores bloqueados", "1 centro con vencidos"]);
        Textos(cut.FindAll(".cliente360-indicadores .ventana-contexto")[0].QuerySelectorAll(".ventana-linea")).Should().BeEquivalentTo([
            "Juan Pérez · Almacén Getafe", "Juan Pérez · Oficinas Bilbao", "Marco Vila · Almacén Getafe"]);
        cut.Markup.Should().NotContain("Bloqueo de la plataforma CAE", "ningún Centro está bloqueado por la plataforma");
    }

    /// <summary>
    /// La tolerancia cambia el resultado de la regla de acceso: tras guardarla, la cabecera vuelve a leer los bloqueos y deja de
    /// contar al Trabajador que la nueva tolerancia ya admite (sin recargar la página).
    /// </summary>
    [Fact]
    public async Task Guardar_una_tolerancia_refresca_el_indicador_de_Trabajadores_bloqueados()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.Add(Centro("Almacén Getafe", EstadoCentro.Faltante, vencidas: 1));
        mediador.Bloqueos.Add(Bloqueo(id, "Almacén Getafe", Guid.NewGuid(), "Juan Pérez"));
        mediador.AlGuardarTolerancia = mediador.Bloqueos.Clear;
        Registrar(mediador);
        var cut = Renderizar(id);
        Textos(cut.FindAll(".cliente360-indicador-boton")).Should().Contain("1 trabajador bloqueado");

        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Configurar tolerancias").Click());
        cut.WaitForAssertion(() => cut.FindAll("select").Should().NotBeEmpty());
        cut.FindAll("select")[0].Change("30");
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").Click());

        cut.WaitForAssertion(() => Textos(cut.FindAll(".cliente360-indicador-boton")).Should().NotContain("1 trabajador bloqueado"));
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
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Otra contratista", null));
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
        mediador.EmpresasPorCliente[idA].Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Segunda contratista A", null));
        mediador.EmpresasPorCliente[idB].Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Segunda contratista B", null));
        Registrar(mediador);
        var cut = Renderizar(idA, "?pestana=empresas");
        cut.WaitForAssertion(() => cut.Find(".fila-relacion-nombre").TextContent.Should().Contain("Ibertec GmbH"));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"clientes/{idB}?pestana=empresas");
        cut.Render(p => p.Add(x => x.ClienteId, idB));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Should().Contain("Aislamientos Nervión S.L."));
        cut.WaitForAssertion(() => cut.FindAll(".fila-relacion-nombre").Select(a => a.TextContent.Trim())
            .Should().Equal(["Montajes Ebro S.L.", "Segunda contratista B"]));
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

    [Fact]
    public void El_anillo_cuenta_centros_sin_incidencias_y_excluye_Sin_gestion_CAE()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([Centro("Vigente", EstadoCentro.Vigente),
            Centro("Por vencer", EstadoCentro.Urgente, proximas: 1), Centro("Sin CAE", EstadoCentro.SinGestionCae)]);
        mediador.TotalCentros = 3;
        Registrar(mediador);
        var cut = Renderizar(id);

        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().Be("1 de 2 centros con gestión CAE sin incidencias");
        cut.Find(".anillo-cumplimiento-detalle").TextContent.Should().Be("1 de 2 centros");
        cut.Find(".cliente360-sin-gestion-cae").TextContent.Should().Contain("SIN GESTIÓN CAE · no cuentan en el anillo").And.Contain("Sin CAE");
        cut.FindAll(".cliente360-sin-gestion-cae .fila-relacion").Should().ContainSingle();
        var sinGestion = cut.Find(".cliente360-sin-gestion-cae");
        sinGestion.HasAttribute("aria-label").Should().BeFalse();
        sinGestion.GetAttribute("aria-labelledby").Should().Be(sinGestion.QuerySelector("h3")!.Id);
        sinGestion.QuerySelector(".lista-relaciones")!.GetAttribute("aria-label").Should().Be("Centros sin gestión CAE");
        cut.FindAll(".lista-relaciones[aria-label='Centros de este Cliente']").Should().ContainSingle();
        cut.FindAll(".fila-relacion-nombre").Select(x => x.TextContent.Trim()).Should().Equal("Vigente", "Por vencer", "Sin CAE");
    }

    [Fact]
    public async Task La_franja_cuenta_antes_de_filtrar_y_conserva_la_busqueda_en_la_URL()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([Centro("Planta vencida", EstadoCentro.Vencido),
            Centro("Planta vigente", EstadoCentro.Vigente), Centro("Almacén", EstadoCentro.Vencido)]);
        mediador.TotalCentros = 3;
        Registrar(mediador);
        var cut = Renderizar(id, "?q=Planta");
        var consultas = mediador.Enviadas.Count;
        cut.Find(".franja-estado [data-estado=Vencido] .franja-estado-recuento").TextContent.Should().Be("1");

        await cut.Find(".franja-estado [data-estado=Vencido]").ClickAsync(new MouseEventArgs());
        cut.FindAll(".fila-relacion-nombre").Select(x => x.TextContent.Trim()).Should().Equal("Planta vencida");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Planta").And.Contain("estado=Vencido");
        cut.Find(".franja-estado [data-estado=Vigente] .franja-estado-recuento").TextContent.Should().Be("1");
        cut.Find(".cliente360-resumen-lista").TextContent.Should().Be("Mostrando 1 de 3 · del peor estado al mejor");
        mediador.Enviadas.Should().HaveCount(consultas, "el filtro actúa sobre la lista ya cargada");
        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().StartWith("1 de 3");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void El_anillo_excluye_al_Centro_vigente_con_bloqueo_de_acceso_visible(bool bloqueado, int sinIncidencias)
    {
        var (id, mediador) = ClienteBase();
        var centro = Centro("Centro documentalmente vigente", EstadoCentro.Vigente, cumplimiento: 100);
        mediador.Centros.Add(centro);
        if (bloqueado)
            mediador.Bloqueos.Add(Bloqueo(id, centro.Nombre, Guid.NewGuid(), "Ana Alba") with { CentroId = centro.Id });
        Registrar(mediador);
        var cut = Renderizar(id);
        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().Be($"{sinIncidencias} de 1 centro con gestión CAE sin incidencias");
        cut.Find(".anillo-cumplimiento-detalle").TextContent.Should().Be($"{sinIncidencias} de 1 centro");
    }

    [Theory]
    [InlineData("es", "centro")]
    [InlineData("ca-ES", "centre")]
    public void El_anillo_de_un_solo_Centro_usa_el_singular_en_es_y_ca(string idioma, string singular)
    {
        var anterior = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(idioma);
            var (id, mediador) = ClienteBase();
            mediador.Centros.Add(Centro("Centro", EstadoCentro.Vigente));
            Registrar(mediador);
            var cut = Renderizar(id);
            cut.Find(".anillo-cumplimiento-detalle").TextContent.Should().Be($"1 de 1 {singular}");
        }
        finally { System.Globalization.CultureInfo.CurrentUICulture = anterior; }
    }

    [Fact]
    public void Sin_lectura_de_bloqueos_no_se_presenta_un_anillo_favorable()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.Add(Centro("Planta", EstadoCentro.Vigente));
        mediador.TotalCentros = 1;
        mediador.FallaLecturaBloqueos = true;
        Registrar(mediador);
        var cut = Renderizar(id);

        cut.FindAll("[data-pieza=anillo]").Should().BeEmpty("no se ha medido la ausencia de bloqueos");
        cut.FindAll(".fila-relacion-nombre").Should().ContainSingle();
    }

    [Fact]
    public void El_estado_en_el_enlace_filtra_la_lista_y_marca_su_contador()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([Centro("Vencida", EstadoCentro.Vencido), Centro("Vigente", EstadoCentro.Vigente)]);
        mediador.TotalCentros = 2;
        Registrar(mediador);
        var cut = Renderizar(id, "?estado=Vencido");

        cut.FindAll(".fila-relacion-nombre").Select(x => x.TextContent.Trim()).Should().Equal("Vencida");
        cut.Find(".franja-estado [data-estado=Vencido]").GetAttribute("aria-pressed").Should().Be("true");
    }

    [Fact]
    public void El_motivo_separa_pendientes_de_vencidos_y_cuenta_trabajadores_bloqueados_sin_duplicarlos()
    {
        var (id, mediador) = ClienteBase();
        var centro = Centro("Planta", EstadoCentro.Faltante) with
        {
            Recuentos = new RecuentosCentroDto([Incidencia(EstadoDocumento.Faltante), Incidencia(EstadoDocumento.Vencido)], [])
        };
        mediador.Centros.Add(centro);
        mediador.TotalCentros = 1;
        var trabajadorId = Guid.NewGuid();
        var bloqueo = Bloqueo(id, centro.Nombre, trabajadorId, "Ana Alba") with { CentroId = centro.Id };
        mediador.Bloqueos.AddRange([bloqueo, bloqueo with { TipoDocumentoId = Guid.NewGuid() }]);
        Registrar(mediador);
        var cut = Renderizar(id);

        cut.Find(".fila-relacion-detalle").TextContent.Should().Contain("1 vencido").And.Contain("1 pendiente")
            .And.Contain("1 trabajador bloqueado").And.NotContain("2 vencidos");
        cut.Find("[data-pieza=anillo]").GetAttribute("aria-label").Should().StartWith("0 de 1");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    public void Empresas_solo_se_ofrece_si_no_hay_una_unica_Empresa_contratista(int cantidad, bool visible)
    {
        var (id, mediador) = ClienteBase();
        mediador.Empresas.AddRange(Enumerable.Range(0, cantidad).Select(i => new EmpresaDeClienteDto(Guid.NewGuid(), $"Contratista {i}", null)));
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=empresas&q=vigo&extra=conservar");
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerSubcontratasDeClienteQuery>().Should().ContainSingle());
        cut.FindAll("[role=tab]").Any(t => t.TextContent.StartsWith("Empresas", StringComparison.Ordinal)).Should().Be(visible);
        cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith(visible ? "Empresas" : "Centros");
        if (visible)
            Textos(cut.FindAll("[role=tab]")).Should().Equal([
                "Centros0 centros", "Empresas2 empresas", "Subcontratas0 subcontratas", "Documentación", "Agenda", "Historial"]);
        if (!visible) cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("Entra como contratista: Contratista 0");
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query.Should().Contain("q=vigo").And.Contain("extra=conservar");
    }

    [Fact]
    public void Pasar_de_una_a_varias_Empresas_contratistas_restituye_la_pestana_sin_recrear_la_ficha()
    {
        var (id, mediador) = ClienteBase();
        mediador.EmpresasPorCliente[id] = [new(Guid.NewGuid(), "Única contratista", null)];
        var otro = Guid.NewGuid();
        mediador.Detalles[otro] = mediador.Detalles[id] with { Id = otro, RazonSocial = "Cliente B" };
        mediador.EmpresasPorCliente[otro] = [new(Guid.NewGuid(), "Primera contratista B", null), new(Guid.NewGuid(), "Segunda contratista B", null)];
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=empresas");
        cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Centros");
        Services.GetRequiredService<NavigationManager>().NavigateTo($"clientes/{otro}?pestana=empresas");
        cut.Render(p => p.Add(x => x.ClienteId, otro));
        cut.WaitForAssertion(() => cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Empresas"));
        cut.FindAll(".fila-relacion-nombre").Select(f => f.TextContent).Should().Equal("Primera contratista B", "Segunda contratista B");
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
            "Centros2 centros", "Subcontratas0 subcontratas", "Documentación",
            "Agenda", "Historial"]);
        cut.FindAll("[role=tab]").Should().HaveCount(5);
        cut.FindAll("[role=tab]").Select(t => t.TextContent).Should().NotContain(t => t.Contains("Blindaje"));
        cut.Find(".pestanas-boton-activa").TextContent.Should().StartWith("Centros");
    }

    [Theory]
    [InlineData("agenda", "Agenda")]
    [InlineData("historial", "Historial")]
    public void Agenda_e_Historial_se_abren_por_URL_con_la_entidad_original(string pestana, string titulo)
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);
        ComponentFactories.AddStub<PestanaAgendaContactos>();
        ComponentFactories.AddStub<PestanaHistorial>();

        var cut = Renderizar(id, $"?pestana={pestana}");

        cut.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be(titulo);
        if (pestana == "agenda")
        {
            var parametros = cut.FindComponent<Stub<PestanaAgendaContactos>>().Instance.Parameters;
            parametros.Get(p => p.PropietarioId).Should().Be(id);
            parametros.Get(p => p.Tipo).Should().Be(TipoPropietarioAgenda.Cliente);
        }
        else
        {
            var parametros = cut.FindComponent<Stub<PestanaHistorial>>().Instance.Parameters;
            parametros.Get(p => p.EntidadId).Should().Be(id);
            parametros.Get(p => p.EntidadTipo).Should().Be("Cliente");
        }
    }

    [Theory]
    [InlineData("documentacion")]
    [InlineData("blindaje42")]
    public void Documentacion_y_el_enlace_antiguo_abren_el_certificado_como_primer_bloque(string pestana)
    {
        var (id, mediador) = ClienteBase();
        mediador.Certificaciones.Add(new BlindajeEmpresaDto(Guid.NewGuid(), "Ibertec GmbH", null, 0, null));
        Registrar(mediador);
        ComponentFactories.AddStub<PestanaDocumentacion>("<div data-testid='documentacion-original'></div>");

        var cut = Renderizar(id, $"?pestana={pestana}");

        cut.Find("[role=tab][aria-selected=true]").TextContent.Trim().Should().Be("Documentación");
        cut.FindAll("[role=tab]").Should().HaveCount(6);
        cut.Find("[role=tab][aria-selected=true]").QuerySelector(".pestanas-contador").Should().BeNull();
        var bloques = cut.Find(".cliente360-documentacion").Children;
        bloques.Should().HaveCount(2);
        bloques[0].QuerySelector(".tarjeta-titulo")!.TextContent.Should()
            .Be("Certificado de estar al corriente con la Seguridad Social");
        bloques[0].TextContent.Should().Contain("Ibertec GmbH").And.Contain("Sin solicitar");
        bloques[1].GetAttribute("data-testid").Should().Be("documentacion-original");
        cut.FindComponent<PestanaBlindaje42>().Instance.EntidadId.Should().Be(id);
        mediador.Enviadas.OfType<ObtenerBlindajeEmpresasDeClienteQuery>().Should()
            .ContainSingle(q => q.ClienteId == id);
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task El_certificado_en_Documentacion_conserva_solicitar_solo_con_escritura(string rol, bool puedeEscribir)
    {
        var (id, mediador) = ClienteBase();
        var empresaId = Guid.NewGuid();
        mediador.Certificaciones.Add(new BlindajeEmpresaDto(empresaId, "Ibertec GmbH", null, 0, null));
        mediador.HistorialCertificaciones.Add(new SolicitudCertificacionTgssDto(Guid.NewGuid(),
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, null,
            CaeManager.Domain.Blindaje42.EstadoBlindaje42.PendienteRespuesta, null, false, null));
        Registrar(mediador, rol);
        ComponentFactories.AddStub<PestanaDocumentacion>("<div data-testid='documentacion-original'></div>");
        var cut = Renderizar(id, "?pestana=documentacion");

        await cut.Find(".cliente360-documentacion .workspace-fila-entidad").ClickAsync(new MouseEventArgs());

        var drawer = cut.FindComponent<DrawerBlindaje42>();
        drawer.Instance.Visible.Should().BeTrue();
        var solicitar = drawer.FindAll("button").Where(b => b.TextContent.Trim() == "Registrar solicitud").ToList();
        solicitar.Should().HaveCount(puedeEscribir ? 1 : 0);
        drawer.FindAll("button").Where(b => b.TextContent.Trim() == "Registrar respuesta")
            .Should().HaveCount(puedeEscribir ? 1 : 0);
        mediador.Enviadas.OfType<ObtenerHistorialCertificacionesTgssQuery>().Should().ContainSingle();
        if (puedeEscribir)
        {
            await solicitar.Single().ClickAsync(new MouseEventArgs());
            mediador.Enviadas.OfType<SolicitarCertificacionTgssCommand>().Should()
                .ContainSingle(c => c.ClienteId == id && c.EmpresaId == empresaId);
        }
        else
        {
            mediador.Enviadas.OfType<SolicitarCertificacionTgssCommand>().Should().BeEmpty();
        }
    }

    [Fact]
    public void La_certificacion_no_se_precarga_desde_Centros()
    {
        var (id, mediador) = ClienteBase();
        Registrar(mediador);

        Renderizar(id);

        mediador.Enviadas.OfType<ObtenerBlindajeEmpresasDeClienteQuery>().Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerHistorialCertificacionesTgssQuery>().Should().BeEmpty();
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

    // Decisión del 2026-10-09: los Centros son del Cliente de la ficha, que es su titular. «Empresa: X» bajo el Centro se
    // leía como si la Empresa fuera la dueña.
    [Fact]
    public void Con_una_sola_Empresa_contratista_la_fila_del_centro_no_nombra_ninguna_Empresa()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([
            Centro("Planta Barakaldo", EstadoCentro.Bloqueado, vencidas: 3, empresa: "Montajes Ebro S.L."),
            Centro("Oficinas Bilbao", EstadoCentro.Vigente, empresa: "Montajes Ebro S.L.")]);
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Montajes Ebro S.L.", null));
        Registrar(mediador);

        var cut = Renderizar(id);

        // La consulta barata siguiente fija que terminó la carga de Empresas; la pestaña redundante ya no se dibuja.
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerSubcontratasDeClienteQuery>().Should().ContainSingle());
        cut.FindAll(".pestanas-boton").Select(b => b.TextContent).Should().NotContain(t => t.StartsWith("Empresas"));
        var filas = cut.FindAll("li.fila-relacion");
        filas[0].QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("3 vencidos");
        filas[1].QuerySelector(".fila-relacion-detalle").Should().BeNull();
        Textos(cut.FindAll(".cliente360-indicadores .ventana-linea")).Should().NotContain(l => l.Contains("Montajes Ebro"));
        cut.FindAll("li.fila-relacion").Select(f => f.TextContent).Should().NotContain(t => t.Contains("Empresa:") || t.Contains("Montajes Ebro"));
    }

    [Fact]
    public void Con_varias_Empresas_contratistas_la_fila_dice_cual_entra_como_contratista_nunca_como_duena()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([
            Centro("Planta Barakaldo", EstadoCentro.Bloqueado, vencidas: 3, proximas: 1, empresa: "Montajes Ebro S.L."),
            Centro("Oficinas Bilbao", EstadoCentro.Vigente)]);
        mediador.Empresas.AddRange([
            new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", null),
            new EmpresaDeClienteDto(Guid.NewGuid(), "Montajes Ebro S.L.", null)]);
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.WaitForAssertion(() => Textos(cut.FindAll("li.fila-relacion .fila-relacion-detalle")).Should().Equal([
            "Entra como contratista: Montajes Ebro S.L. · 3 vencidos · 1 próximo",
            "Entra como contratista: Ibertec GmbH"]));
        Textos(cut.FindAll(".cliente360-indicadores .ventana-contexto")[0].QuerySelectorAll(".ventana-linea"))
            .Should().Equal(["Planta Barakaldo · Entra como contratista: Montajes Ebro S.L."]);
        cut.FindAll("li.fila-relacion").Select(f => f.TextContent).Should().NotContain(t => t.Contains("Empresa:"));
    }

    [Fact]
    public void Al_pasar_a_un_Cliente_con_una_Empresa_contratista_se_nombra_en_la_cabecera_y_no_en_las_filas()
    {
        var (idA, mediador) = ClienteBase();
        mediador.CentrosPorCliente[idA] = [Centro("Planta Barakaldo", EstadoCentro.Vigente, empresa: "Montajes Ebro S.L.")];
        mediador.EmpresasPorCliente[idA] = [
            new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", null),
            new EmpresaDeClienteDto(Guid.NewGuid(), "Montajes Ebro S.L.", null)];
        var idB = Guid.NewGuid();
        mediador.Detalles[idB] = new ClienteDetalleDto(idB, "Aislamientos Nervión S.L.", "B-99.000.111", false, null, Alta, null, Guid.NewGuid());
        mediador.Resumenes[idB] = new ResumenClienteDto(idB, "Aislamientos Nervión S.L.", "B-99.000.111", false, Alta, null, 1, 7);
        mediador.CentrosPorCliente[idB] = [Centro("Nave logística Tudela", EstadoCentro.Vigente, empresa: "Montajes Ebro S.L.")];
        mediador.EmpresasPorCliente[idB] = [new EmpresaDeClienteDto(Guid.NewGuid(), "Montajes Ebro S.L.", null)];
        Registrar(mediador);
        var cut = Renderizar(idA);
        cut.WaitForAssertion(() => cut.Find("li.fila-relacion .fila-relacion-detalle").TextContent
            .Should().Be("Entra como contratista: Montajes Ebro S.L."));

        Services.GetRequiredService<NavigationManager>().NavigateTo($"clientes/{idB}");
        cut.Render(p => p.Add(x => x.ClienteId, idB));

        cut.WaitForAssertion(() => cut.Find("li.fila-relacion .fila-relacion-nombre").TextContent.Should().Contain("Nave logística Tudela"));
        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerEmpresasDeClienteQuery>().Should().Contain(new ObtenerEmpresasDeClienteQuery(idB)));
        cut.FindAll("li.fila-relacion .fila-relacion-detalle").Should().BeEmpty();
        cut.FindAll("li.fila-relacion").Select(f => f.TextContent)
            .Should().NotContain(t => t.Contains("Entra como contratista"));
        cut.Find(".cabecera-identidad-datos").TextContent.Should().Contain("Entra como contratista: Montajes Ebro S.L.");
    }

    // Una sola medida de pastilla en la ficha (13 px): la pequeña de 11 px convivía con la normal.
    [Fact]
    public void Ninguna_pastilla_de_la_ficha_usa_la_medida_pequena()
    {
        var (id, mediador) = ClienteBase(critico: true);
        mediador.Centros.AddRange([
            Centro("Planta Barakaldo", EstadoCentro.Bloqueado, vencidas: 2),
            Centro("Almacén Getafe", EstadoCentro.Vencido, vencidas: 1, proximas: 1)]);
        Registrar(mediador);

        var cut = Renderizar(id);

        cut.FindAll(".badge").Should().HaveCountGreaterThan(4, "Crítico, tres indicadores y dos estados de Centro");
        cut.FindAll(".badge-pequeno").Should().BeEmpty();
    }

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

        cut.Find(".cliente360-resumen-lista").TextContent.Should().Be("Mostrando 2 de 2 · del peor estado al mejor");
        var filas = cut.FindAll("li.fila-relacion");
        filas.Select(f => f.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href"))
            .Should().Equal([$"/centros/{bloqueado.Id}", $"/centros/{vigente.Id}"], "el orden es el de la consulta: peor primero");
        filas[0].QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("3 vencidos · 1 próximo");
        filas[1].QuerySelector(".fila-relacion-detalle").Should().BeNull("sin recuentos ni Empresa que distinguir no hay segunda línea");
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

        cut.Find(".cliente360-resumen-lista").TextContent.Should().Be("Se muestran los 2 centros con peor estado de 250 · del peor estado al mejor");
        cut.FindAll("[data-pieza=anillo]").Should().BeEmpty("una lista truncada no define una fracción global");
        cut.Find("[data-recuentos-parciales]").TextContent.Should().Be("Los recuentos y filtros corresponden a los 2 Centros cargados de 250.");
        cut.Find(".cliente360-indicador-boton").TextContent.Trim().Should().Be("Bloqueo de la plataforma CAE en 1 de 250 centros");
    }

    [Fact]
    public void Cada_empresa_enlaza_a_su_pagina_con_su_CIF_y_su_boton_360_abre_su_panel()
    {
        var (id, mediador) = ClienteBase();
        var empresa = new EmpresaDeClienteDto(Guid.NewGuid(), "Ibertec GmbH", "B-12345678");
        mediador.Empresas.Add(empresa);
        mediador.Empresas.Add(new EmpresaDeClienteDto(Guid.NewGuid(), "Otra contratista", null));
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=empresas");

        var fila = cut.Find("li.fila-relacion");
        fila.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("href").Should().Be($"/empresas/{empresa.Id}");
        fila.QuerySelector("a.fila-relacion-nombre")!.GetAttribute("title").Should().Be("Ir a la página de Ibertec GmbH");
        fila.QuerySelector(".fila-relacion-detalle")!.TextContent.Should().Be("B-12345678");

        fila.QuerySelector("button.boton-360")!.Click();

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(
            new WorkspaceFrame(EntidadWorkspace.Empresa, empresa.Id, "Ibertec GmbH", "informacion"));
    }

    [Fact]
    public void El_nombre_de_una_subcontrata_enlaza_a_su_pagina_y_el_boton_360_abre_el_panel()
    {
        var (id, mediador) = ClienteBase();
        var subcontrata = new SubcontrataDeClienteDto(Guid.NewGuid(), "Andamios Cantábrico S.L.");
        mediador.Subcontratas.Add(subcontrata);
        Registrar(mediador);
        var cut = Renderizar(id, "?pestana=subcontratas");

        cut.Find("li.fila-relacion a.fila-relacion-nombre").GetAttribute("href").Should().Be($"/subcontratas/{subcontrata.Id}");

        cut.Find("li.fila-relacion button.boton-360").Click();

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
        Services.ConEnlaceProfundoOtraEmpresa();
        var mediador = Registrar(new MediatorFalso());
        var id = Guid.NewGuid();

        var cut = Renderizar(id);

        cut.Find(".estado-vacio").TextContent.Should()
            .Contain("No pudimos cargar este Cliente").And.Contain("Puede que ya no exista o que no tengas acceso.");
        cut.FindAll("[data-pieza=cabecera-identidad]").Should().BeEmpty();
        // Del cliente pide solo su ficha; la lista de empresas autorizadas es del aviso de enlace profundo (§ 4.5).
        mediador.Enviadas.Where(e => e is not CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados.ObtenerClientesAutorizadosQuery)
            .Should().ContainSingle().Which.Should().BeOfType<ObtenerClientePorIdQuery>();

        mediador.Detalles[id] = new ClienteDetalleDto(id, "Refrielectric S.A.", "A-48.220.917", false, null, Alta, null, Guid.NewGuid());
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").Click();

        cut.WaitForAssertion(() => cut.Find("[data-pieza=cabecera-identidad] h1").TextContent.Trim().Should().Be("Refrielectric S.A."));
    }

    // ── Acciones y lateral ────────────────────────────────────────────────

    [Fact]
    public async Task Con_escritura_Editar_cliente_y_los_Editar_del_lateral_abren_el_panel()
    {
        var (id, mediador) = ClienteBase(notas: "Llamar antes de ir.");
        Registrar(mediador);
        var cut = Renderizar(id);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        cut.Find("[data-pieza=lateral] [data-pieza=nota-interna]").TextContent.Should().Contain("Nota interna");
        cut.Find(".nota-interna-texto").TextContent.Should().Be("Llamar antes de ir.");
        cut.Find(".nota-interna-pie").TextContent.Should().Be("No la ve el cliente.");

        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == "Editar Cliente").ClickAsync(new MouseEventArgs());
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
        cut.Find("[data-pieza=lateral] [data-pieza=nota-interna] [data-vacia=true]").TextContent.Should().Be("Sin nota interna.");
    }

    [Fact]
    public void Al_Usuario_de_Cliente_no_se_le_pinta_la_caja_de_la_nota_interna()
    {
        // El DTO lleva la nota a propósito y la bandera del servidor dice que no
        // se ve (el handler decide así para este rol): aquí se mide que la página
        // obedece la bandera y no pinta la caja.
        var (id, mediador) = ClienteBase(notas: "Llamar antes de ir.");
        mediador.Detalles[id] = mediador.Detalles[id] with { NotaInternaVisible = false };
        Registrar(mediador, Roles.Cliente);

        var cut = Renderizar(id);

        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().Contain("Refrielectric S.A.", "la ficha de su Cliente sí se le pinta");
        cut.FindAll("[data-pieza=nota-interna]").Should().BeEmpty();
        cut.FindAll(".nota-interna-pie").Should().BeEmpty();
        cut.Markup.Should().NotContain("Llamar antes de ir.");
    }

    [Fact]
    public void El_rol_Consulta_del_lado_de_gestion_si_ve_la_nota_interna()
    {
        var (id, mediador) = ClienteBase(notas: "Llamar antes de ir.");
        Registrar(mediador, Roles.Consulta);

        var cut = Renderizar(id);

        cut.Find("[data-pieza=lateral] [data-pieza=nota-interna]").TextContent.Should().Contain("Nota interna");
        cut.Find(".nota-interna-texto").TextContent.Should().Be("Llamar antes de ir.");
    }

    // ── Buscador de la pestaña Centros y ?q= ──────────────────────────────

    private static List<string> NombresDeCentro(IRenderedComponent<ClienteDetalle> cut) =>
        Textos(cut.FindAll(".fila-relacion-nombre"));

    private (Guid Id, MediatorFalso Mediador) ClienteConTresCentros()
    {
        var (id, mediador) = ClienteBase();
        mediador.Centros.AddRange([
            Centro("Almacén Getafe", EstadoCentro.Faltante, vencidas: 1),
            Centro("Planta Vigo", EstadoCentro.Bloqueado, vencidas: 2),
            Centro("Almacén Vigo", EstadoCentro.Vigente)]);
        Registrar(mediador);
        return (id, mediador);
    }

    [Fact]
    public async Task El_buscador_de_Centros_filtra_por_nombre_lo_deja_en_la_URL_y_no_toca_el_contador_ni_los_indicadores()
    {
        var (id, _) = ClienteConTresCentros();
        var cut = Renderizar(id);
        var indicadoresAntes = Textos(cut.FindAll(".cliente360-indicador-boton"));
        indicadoresAntes.Should().NotBeEmpty("el control: sin indicadores, «no cambian» no probaría nada");

        await cut.Find("input[type=search]").InputAsync("VIGO");

        cut.WaitForAssertion(() => NombresDeCentro(cut).Should().BeEquivalentTo(["Planta Vigo", "Almacén Vigo"]));
        cut.Find(".cliente360-resumen-lista").TextContent.Should().Contain("Mostrando 2 de 3");
        cut.FindAll("[role=tab]").First().TextContent.Should().Be("Centros3 centros");
        Textos(cut.FindAll(".cliente360-indicador-boton")).Should().Equal(indicadoresAntes);
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query.Should().Be("?q=VIGO");
    }

    [Fact]
    public void Un_enlace_con_q_abre_la_lista_de_Centros_ya_buscada_junto_a_la_pestana()
    {
        var (id, _) = ClienteConTresCentros();

        var cut = Renderizar(id, "?pestana=centros&q=getafe");

        NombresDeCentro(cut).Should().Equal(["Almacén Getafe"]);
        cut.Find("input[type=search]").GetAttribute("value").Should().Be("getafe");
    }

    [Fact]
    public async Task Sin_coincidencias_Quitar_filtros_devuelve_los_Centros_y_limpia_la_URL()
    {
        var (id, _) = ClienteConTresCentros();
        var cut = Renderizar(id, "?q=no-existe");
        NombresDeCentro(cut).Should().BeEmpty();
        cut.Find(".cliente360-resumen-lista").TextContent.Should().Contain("Mostrando 0 de 3");

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Quitar filtros").ClickAsync(new MouseEventArgs());

        NombresDeCentro(cut).Should().HaveCount(3);
        cut.Find("input[type=search]").GetAttribute("value").Should().BeEmpty();
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query.Should().BeEmpty("si la URL conservara q, recargar lo repondría");
        cut.FindAll("button").Should().NotContain(b => b.TextContent.Trim() == "Quitar filtros");
    }

    [Fact]
    public async Task Cambiar_de_pestana_conserva_el_texto_buscado_en_la_URL()
    {
        var (id, _) = ClienteConTresCentros();
        var cut = Renderizar(id, "?q=vigo");

        await cut.FindAll("[role=tab]").Single(t => t.TextContent.StartsWith("Empresas")).ClickAsync(new MouseEventArgs());

        var consulta = new Uri(Services.GetRequiredService<NavigationManager>().Uri).Query;
        consulta.Should().Contain("pestana=empresas").And.Contain("q=vigo");
    }

    [Fact]
    public void Con_la_lista_de_Centros_cortada_el_resumen_de_la_busqueda_sigue_diciendo_que_no_estan_todos()
    {
        var (id, mediador) = ClienteConTresCentros();
        mediador.TotalCentros = 250;

        var cut = Renderizar(id, "?q=no-existe");

        cut.Find(".cliente360-resumen-lista").TextContent.Should()
            .Contain("Mostrando 0 de los 3 centros con peor estado").And.Contain("tiene 250");
    }
}
