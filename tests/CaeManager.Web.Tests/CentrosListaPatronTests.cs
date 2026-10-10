using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Centros.Queries.ObtenerEmpresasDeCentrosVisibles;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lote 3 del selector de Tenant y patrón de lista en <c>/centros</c> tras el rediseño de listados
/// (fase 1): el estado 4a, la carga asíncrona de la empresa, la cabecera de una línea, la barra de
/// filtros en pastillas con chips, la agrupación por Cliente empresarial, las filas tintadas, la
/// cabecera de columnas, la fila sin menú «⋯», la vista rápida y el paginador.
/// El acordeón de asignaciones se sustituye por un stub: aquí se mide lo que pinta la PÁGINA.
/// </summary>
public class CentrosListaPatronTests : BunitContext
{
    public CentrosListaPatronTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        ComponentFactories.AddStub<AcordeonAsignacionesCentro>();
        Services.AddLocalization();
    }

    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaNorte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaSur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    /// <summary>Mediador que responde por tipo, apunta lo enviado y puede retener la resolución de la empresa.</summary>
    private sealed class Mediador : IMediator
    {
        public List<object> Enviadas { get; } = [];
        public List<CentroListaDto> Centros { get; } = [];
        public Dictionary<Guid, IReadOnlyList<VisitaResumenDto>> Visitas { get; } = [];
        public List<ClienteSelectorDto> ClientesSelector { get; } = [];
        public List<EmpresaSelectorDto> EmpresasSelector { get; } = [];

        /// <summary>Opciones del filtro «Empresa»: las Empresas de los Centros visibles (no el selector del alta).</summary>
        public List<EmpresaDeCentroDto> EmpresasDeCentros { get; } = [];

        /// <summary>Si se fija, las opciones del filtro «Empresa» esperan a esta tarea.</summary>
        public Task? RetenerEmpresasDeCentros { get; set; }

        /// <summary>Las opciones del filtro «Empresa» fallan (la consulta lanza).</summary>
        public bool FallarEmpresasDeCentros { get; set; }

        /// <summary>Por defecto, un usuario mono-Tenant: sin selector ni cabecera de empresa gestionada.</summary>
        public List<ClienteAutorizadoDto> Autorizados { get; } = [new(Guid.NewGuid(), "Propia", EsOrigen: true)];

        /// <summary>Si se fija, la respuesta de la lista de Tenants autorizados espera a esta tarea (mediador asíncrono).</summary>
        public Task? RetenerAutorizados { get; set; }

        /// <summary>El token con el que la página pidió la lista de Tenants autorizados.</summary>
        public CancellationToken? TokenDeAutorizados { get; private set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerClientesAutorizadosQuery)
                TokenDeAutorizados = cancellationToken;
            if (request is ObtenerClientesAutorizadosQuery && RetenerAutorizados is { } espera)
                await espera;
            if (request is ObtenerEmpresasDeCentrosVisiblesQuery && RetenerEmpresasDeCentros is { } esperaEmpresas)
                await esperaEmpresas;
            if (request is ObtenerEmpresasDeCentrosVisiblesQuery && FallarEmpresasDeCentros)
                throw new InvalidOperationException("Fallo simulado de las opciones de Empresa.");

            return (TResponse)(request switch
            {
                ObtenerClientesAutorizadosQuery => (object)(IReadOnlyList<ClienteAutorizadoDto>)Autorizados.ToList(),
                ObtenerCentrosQuery q => Filtrar(q),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)Visitas,
                ObtenerClientesParaSelectorQuery => (IReadOnlyList<ClienteSelectorDto>)ClientesSelector.ToList(),
                ObtenerEmpresasParaSelectorQuery => (IReadOnlyList<EmpresaSelectorDto>)EmpresasSelector.ToList(),
                ObtenerEmpresasDeCentrosVisiblesQuery => (IReadOnlyList<EmpresaDeCentroDto>)EmpresasDeCentros.ToList(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });
        }

        /// <summary>
        /// Filtra por Cliente empresarial y por Empresa como la consulta real (que lo prueba
        /// BusquedaYFiltrosDeListadosTests contra Postgres): si la página no enviara el filtro,
        /// se verían todos.
        /// </summary>
        private ResultadoPaginado<CentroListaDto> Filtrar(ObtenerCentrosQuery q)
        {
            var filas = Centros
                .Where(c => q.ClienteId is null || c.ClienteId == q.ClienteId)
                .Where(c => q.EmpresaId is null || c.EmpresaId == q.EmpresaId)
                .ToList();
            return new ResultadoPaginado<CentroListaDto>(filas, filas.Count, q.Pagina, q.TamanoPagina);
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static CentroListaDto Centro(string nombre, EstadoCentro estado = EstadoCentro.Vigente) => new(
        Guid.NewGuid(), nombre, "C-001", Guid.NewGuid(), "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", estado,
        CumplimientoPorcentaje: 87, RecuentosCentroDto.Vacio);

    private static readonly Guid ClienteOrion = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ClientePegaso = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaMontajes = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaLimpiezas = Guid.Parse("cccccccc-0000-0000-0000-000000000002");

    /// <summary>Un Centro de un Cliente empresarial y una Empresa concretos (para agrupar y filtrar).</summary>
    private static CentroListaDto CentroDe(string nombre, Guid clienteId, Guid empresaId, EstadoCentro estado = EstadoCentro.Vigente) => new(
        Guid.NewGuid(), nombre, "C-001",
        clienteId, clienteId == ClienteOrion ? "Orion Cliente S.L." : "Pegaso Cliente S.L.",
        empresaId, empresaId == EmpresaMontajes ? "Montajes Norte S.L." : "Limpiezas Sur S.L.", estado,
        CumplimientoPorcentaje: 87, RecuentosCentroDto.Vacio);

    /// <summary>Dos Clientes empresariales: Orion con dos Centros (uno vencido, otro urgente) y Pegaso con uno al día.</summary>
    private static Mediador ConDosClientes()
    {
        var mediador = ConCentros(
            CentroDe("Almacén Vigo", ClienteOrion, EmpresaMontajes, EstadoCentro.Vencido),
            CentroDe("Planta Murcia", ClienteOrion, EmpresaMontajes, EstadoCentro.Urgente),
            CentroDe("Planta Bilbao", ClientePegaso, EmpresaLimpiezas));
        mediador.ClientesSelector.AddRange([new(ClienteOrion, "Orion Cliente S.L."), new(ClientePegaso, "Pegaso Cliente S.L.")]);
        // El filtro «Empresa» lee las Empresas de los Centros visibles; el selector del alta (alcance de
        // gestión) queda vacío, como para un usuario de portal: si la página lo usara, no habría opciones.
        mediador.EmpresasDeCentros.AddRange([new(EmpresaMontajes, "Montajes Norte S.L."), new(EmpresaLimpiezas, "Limpiezas Sur S.L.")]);
        return mediador;
    }

    /// <summary>Un Operador CAE externo con dos Tenants beneficiarios; el origen no está gestionado por Operación.</summary>
    private static Mediador ConCartera(bool origenGestionado, params CentroListaDto[] centros)
    {
        var mediador = new Mediador();
        mediador.Centros.AddRange(centros);
        mediador.Autorizados.Clear();
        mediador.Autorizados.AddRange(
        [
            new ClienteAutorizadoDto(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: origenGestionado),
            new ClienteAutorizadoDto(EmpresaNorte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(EmpresaSur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
        ]);
        return mediador;
    }

    private static Mediador ConCentros(params CentroListaDto[] centros)
    {
        var mediador = new Mediador();
        mediador.Centros.AddRange(centros);
        return mediador;
    }

    private IRenderedComponent<Centros> Renderizar(Mediador mediador, string url = "centros", Guid? tenantSeleccionado = null) =>
        RenderizarConGruposContraidos(mediador, url, tenantSeleccionado).AbrirGruposDeCentros();

    /// <summary>Como la ve el usuario al llegar: agrupada por Cliente empresarial y con los grupos contraídos.</summary>
    private IRenderedComponent<Centros> RenderizarConGruposContraidos(Mediador mediador, string url = "centros", Guid? tenantSeleccionado = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba(tenantSeleccionado));
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<Centros>();
    }

    // ------------------------------------------------- lote 3 del selector: empresa gestionada activa

    /// <summary>
    /// Rediseño de listados, fase 1: la cabecera de una línea ya no repite qué empresa gestionada
    /// está activa —lo dice el selector de la barra lateral—, pero la lista sí es la de esa empresa.
    /// </summary>
    [Fact]
    public void Con_varias_empresas_gestionadas_la_lista_es_la_de_la_activa_sin_rotulo_en_la_cabecera()
    {
        var cut = Renderizar(ConCartera(origenGestionado: false, Centro("Centro Norte")), tenantSeleccionado: EmpresaSur);

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Find("header.cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("1");
        cut.Markup.Should().Contain("Centro Norte");
    }

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_cabecera_de_empresa_gestionada()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.FindAll(".tarjeta-fila-acordeon").Should().ContainSingle("la lista se pinta como siempre");
    }

    [Fact]
    public void Sin_empresa_elegida_y_con_el_origen_sin_gestionar_pide_elegir_y_no_muestra_datos_del_origen()
    {
        var mediador = ConCartera(origenGestionado: false, Centro("Centro del origen"));

        var cut = Renderizar(mediador);

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty("en el estado 4a no hay empresa activa que nombrar");
        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("sus descargas exportarían los datos del origen");
        cut.Markup.Should().NotContain("+ Nuevo centro").And.NotContain("Centro del origen");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty("no se piden los centros de la organización de origen");
    }

    [Fact]
    public async Task Retirar_la_pagina_cancela_la_resolucion_de_la_empresa_activa()
    {
        var puerta = new TaskCompletionSource();
        var mediador = ConCartera(origenGestionado: false, Centro("Centro Sur"));
        mediador.RetenerAutorizados = puerta.Task;
        var cut = Renderizar(mediador, tenantSeleccionado: EmpresaSur);

        mediador.TokenDeAutorizados.Should().NotBeNull("la resolución se pidió con el token del ciclo de vida");
        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeFalse("la página sigue montada");

        await DisposeComponentsAsync();

        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeTrue("al retirar la página nadie espera la respuesta");
        puerta.SetResult();
    }

    [Fact]
    public async Task Si_la_resolucion_termina_tras_retirar_la_pagina_no_se_pide_ninguna_lista()
    {
        var puerta = new TaskCompletionSource();
        var mediador = ConCartera(origenGestionado: false, Centro("Centro Sur"));
        mediador.RetenerAutorizados = puerta.Task; // este mediador ignora el token, como una dependencia mal portada
        Renderizar(mediador, tenantSeleccionado: EmpresaSur);

        await DisposeComponentsAsync();
        puerta.SetResult();
        await Task.Delay(300); // la continuación corre en el hilo del pool: se le da margen antes de afirmar la ausencia

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty("la página ya no existe: no hay carga posterior");
    }

    /// <summary>
    /// Patrón de listados (decisión 2026-10-08): la fila no lleva menú «⋯»; su última columna es el icono
    /// 360, un enlace real a la página del Centro con nombre accesible propio de cada fila.
    /// </summary>
    [Fact]
    public void La_fila_no_lleva_menu_y_su_ultima_columna_es_el_icono_360_con_nombre_propio()
    {
        var norte = Centro("Centro Norte");
        var sur = Centro("Centro Sur");
        var cut = Renderizar(ConCentros(norte, sur));

        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(2, "control positivo: las dos filas están pintadas");
        cut.FindAll(".lista-filas-acordeon .menu-acciones-disparador").Should().BeEmpty("la fila no lleva menú «⋯»");
        var iconos = cut.FindAll(".tarjeta-fila-acordeon-acciones a.boton-360-pagina");
        iconos.Select(i => i.GetAttribute("href")).Should().Equal($"/centros/{norte.Id}", $"/centros/{sur.Id}");
        iconos.Select(i => i.GetAttribute("aria-label")).Should().Equal(
            "Abrir la ficha 360 de Centro Norte", "Abrir la ficha 360 de Centro Sur");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var cut = Renderizar(ConCartera(origenGestionado: true, Centro("Centro del origen")));

        cut.Markup.Should().NotContain("Selecciona una empresa de tu cartera");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().Contain("Centro del origen");
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista_ni_se_ofrece_la_exportacion()
    {
        var puerta = new TaskCompletionSource();
        var mediador = ConCartera(origenGestionado: false, Centro("Centro Sur"));
        mediador.RetenerAutorizados = puerta.Task;

        var cut = Renderizar(mediador, tenantSeleccionado: EmpresaSur);

        cut.FindAll(".barra-filtros-pastillas").Should().BeEmpty();
        cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Should().BeEmpty("el «⋯» lleva las descargas");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty();

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas").Should().NotBeEmpty());
        cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Should().ContainSingle("el «⋯» con las dos descargas");
        cut.AbrirGruposDeCentros();
        cut.Markup.Should().Contain("Centro Sur");
    }

    [Fact]
    public void Un_enlace_profundo_no_abre_el_alta_mientras_hay_que_elegir_empresa()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador, url: "centros?accion=crear");

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        mediador.Enviadas.Select(r => r.GetType().Name).Should().OnlyContain(
            n => n == nameof(ObtenerClientesAutorizadosQuery),
            "el alta escribiría en el Tenant de origen y solo la consulta de la cartera es legítima aquí");
    }

    [Fact]
    public void Un_cambio_de_filtro_en_la_url_no_carga_la_lista_mientras_hay_que_elegir_empresa()
    {
        var mediador = ConCartera(origenGestionado: false, Centro("Centro del origen"));
        var cut = Renderizar(mediador);
        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera", "control positivo: estamos en el 4a");

        // OnParametersSetAsync re-sincroniza desde la URL: sin la guarda, esto cargaría los centros del origen.
        cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("centros?q=origen"));

        cut.Markup.Should().NotContain("Centro del origen");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty();
    }

    [Fact]
    public void La_lista_nunca_pide_consultas_agregadas_entre_Tenants()
    {
        // I5: «Todos» existe solo en Mi trabajo y Dashboard; una lista trabaja sobre un Tenant.
        var mediador = ConCartera(origenGestionado: false, Centro("Centro Norte"));

        var cut = Renderizar(mediador, tenantSeleccionado: EmpresaNorte);

        cut.FindAll(".tarjeta-fila-acordeon").Should().NotBeEmpty("control positivo: la lista se pintó");
        mediador.Enviadas.Select(r => r.GetType().Namespace ?? "").Should().NotContain(
            n => n.Contains(".Dashboard") || n.Contains(".MiTrabajo") || n.Contains(".Bandeja"));
    }

    // ------------------------------------------------- patrón único de lista

    /// <summary>
    /// Rediseño de listados, fase 1: cabecera de una línea con el contador, el ☑, el «⋯» con las dos
    /// descargas y UNA primaria. Sin antetítulo: «Negocio» ya está en las migas.
    /// </summary>
    [Fact]
    public async Task La_cabecera_es_de_una_linea_con_contador_seleccion_menu_y_una_sola_primaria()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte"), Centro("Centro Sur")));

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker").Should().BeNull();
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Centros");
        cabecera.QuerySelector(".cabecera-listado-contador")!.TextContent.Trim().Should().Be("2");
        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.QuerySelectorAll("a").Should().BeEmpty("las descargas viven dentro del «⋯»");
        acciones.QuerySelectorAll("button").Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Equal("Selección múltiple", "Atajos de teclado", "Más acciones", "+ Nuevo centro");

        await cut.Find("header.cabecera-pagina .menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        cut.FindAll("header.cabecera-pagina a.menu-acciones-item").Select(i => (i.TextContent.Trim(), i.GetAttribute("href")))
            .Should().SatisfyRespectively(
                vista => { vista.Item1.Should().StartWith("Exportar esta vista (filas: "); vista.Item2.Should().StartWith("/centros/exportar.xlsx"); },
                todo => todo.Should().Be(("Exportar todo", "/centros/exportar.xlsx")),
                asignaciones => asignaciones.Should().Be(("Exportar asignaciones", "/asignaciones/exportar.xlsx")));
    }

    [Fact]
    public void Ninguna_accion_de_la_pagina_queda_dentro_de_la_barra_de_filtros()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        var barra = cut.Find(".barra-filtros-pastillas");
        barra.QuerySelectorAll("a").Should().BeEmpty();
        barra.TextContent.Should().NotContain("Exportar").And.NotContain("+ Nuevo centro");
        cut.FindAll(".barra-trabajo-centros").Should().BeEmpty("la fila única de acciones ya no existe");
    }

    [Fact]
    public void El_buscador_es_Filtrar_esta_pantalla_y_promete_lo_que_busca_la_consulta()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        var buscador = cut.Find(".barra-filtros-pastillas input[type=text]");
        buscador.GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: centro, código, Cliente o empresa",
            "ObtenerCentrosQuery busca en los cuatro; los E2E usan este texto");
        buscador.HasAttribute("data-filtro-pantalla").Should().BeTrue("es lo que enfoca la tecla f (atajos-lista.js)");
    }

    /// <summary>
    /// El Estado ya no es una pastilla: es la franja de estado, encima de la tabla, con un botón por rótulo.
    /// Urgente y Próximo son un solo botón, «Por vencer».
    /// </summary>
    [Fact]
    public void La_pastilla_primaria_es_Cliente_empresarial_el_Estado_va_en_la_franja_y_Empresa_en_Mas_filtros()
    {
        var cut = Renderizar(ConDosClientes());

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Select(p => p.GetAttribute("aria-label"))
            .Should().Equal("Cliente", "Más filtros");
        cut.RotulosDeFranja().Should().Equal(
            "Todos", "Bloqueo de la plataforma CAE", "Vencido", "Pendiente", "Por vencer", "Vigente", "No requiere gestión CAE");
        cut.MarcadosEnFranja().Should().Equal(["Todos"], "sin filtro de estado, el marcado es «Todos»");
    }

    [Fact]
    public void La_busqueda_sale_como_chip_el_estado_marcado_en_la_franja_y_desmarcarlo_lo_quita_de_la_url()
    {
        var mediador = ConCentros(Centro("Centro Norte"));
        var cut = Renderizar(mediador, url: "centros?q=norte&estado=Vencido");

        var chips = cut.FindAll(".barra-filtros-pastillas .chip-filtro").Select(c => c.TextContent.Trim()).ToList();
        chips.Should().ContainSingle("el estado ya no tiene chip: se ve marcado en la franja").Which.Should().Contain("norte");
        cut.MarcadosEnFranja().Should().Equal("Vencido");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().Estados.Should().Equal(EstadoCentro.Vencido);

        cut.BotonDeFranja("Vencido").Click();

        var url = Services.GetRequiredService<NavigationManager>().Uri;
        url.Should().NotContain("estado=").And.Contain("q=norte");
        cut.MarcadosEnFranja().Should().Equal("Todos");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().Estados.Should().BeNullOrEmpty();
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().ContainSingle();
    }

    /// <summary>
    /// «Por vencer» es un solo botón para dos estados de código: marca Urgente y Próximo a la vez, en la URL
    /// (<c>estado=Urgente,Proximo</c>) y en la consulta, y admite otro estado marcado a su lado.
    /// </summary>
    [Fact]
    public void Marcar_Por_vencer_en_la_franja_manda_Urgente_y_Proximo_en_la_url_y_en_la_consulta()
    {
        var mediador = ConCentros(Centro("Centro Norte"));
        var cut = Renderizar(mediador);

        cut.BotonDeFranja("Por vencer").Click();

        var navegacion = Services.GetRequiredService<NavigationManager>();
        Uri.UnescapeDataString(navegacion.Uri).Should().EndWith("estado=Urgente,Proximo");
        var consulta = mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last();
        consulta.Estados.Should().Equal(EstadoCentro.Urgente, EstadoCentro.Proximo);
        consulta.Estado.Should().BeNull("el filtro de un solo estado ya no se usa desde la página");
        cut.MarcadosEnFranja().Should().Equal("Por vencer");

        cut.BotonDeFranja("Vencido").Click();

        Uri.UnescapeDataString(navegacion.Uri).Should().EndWith("estado=Urgente,Proximo,Vencido");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().Estados
            .Should().Equal(EstadoCentro.Urgente, EstadoCentro.Proximo, EstadoCentro.Vencido);
        cut.MarcadosEnFranja().Should().Equal("Vencido", "Por vencer");
    }

    [Fact]
    public void Limpiar_todo_quita_los_cuatro_filtros_de_la_url_en_una_sola_navegacion()
    {
        var url = $"centros?q=norte&estado=Vencido&cliente={ClienteOrion}&empresa={EmpresaMontajes}";
        var cut = Renderizar(ConDosClientes(), url: url);
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().HaveCount(3, "control positivo: búsqueda, Cliente y empresa llegaron de la URL");
        cut.MarcadosEnFranja().Should().Equal(["Vencido"], "control positivo: el cuarto filtro, el estado, llegó de la URL y está marcado en la franja");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var navegaciones = 0;
        navegacion.LocationChanged += (_, _) => navegaciones++;

        cut.Find(".limpiar-filtros-barra").Click();

        navegacion.Uri.Should().NotContain("q=").And.NotContain("estado=").And.NotContain("cliente=").And.NotContain("empresa=");
        navegaciones.Should().Be(1);
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().BeEmpty();
        cut.MarcadosEnFranja().Should().Equal("Todos");
    }

    /// <summary>
    /// El estado no tiene chip, pero sigue siendo un filtro: con solo el estado marcado, «Limpiar todo» aparece
    /// y lo quita. Si el estado dejara de contar como filtro activo, la franja sería el único modo de deshacerlo.
    /// </summary>
    [Fact]
    public void Con_solo_el_estado_marcado_Limpiar_todo_aparece_y_lo_quita()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")), url: "centros?estado=Vencido");
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Should().BeEmpty("control: el estado no pinta chip");

        cut.Find(".limpiar-filtros-barra").Click();

        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("estado=");
        cut.MarcadosEnFranja().Should().Equal("Todos");
    }

    /// <summary>
    /// La pastilla «Cliente empresarial» viaja en la consulta y en la URL como «cliente» —no como el
    /// «clienteId» del alta encadenada—, y su chip dice cuál.
    /// </summary>
    [Fact]
    public void La_pastilla_Cliente_empresarial_filtra_la_consulta_y_la_url()
    {
        var mediador = ConDosClientes();
        var cut = Renderizar(mediador);

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Single(p => p.GetAttribute("aria-label") == "Cliente").Click();
        cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Single(i => i.TextContent.Trim() == "Pegaso Cliente S.L.").Click();

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().ClienteId.Should().Be(ClientePegaso);
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"cliente={ClientePegaso}").And.NotContain("clienteId=");
        cut.AbrirGruposDeCentros();
        cut.FindAll(".lista-filas-acordeon .enlace-nombre-fila").Select(b => b.TextContent.Trim()).Should().Equal("Planta Bilbao");
        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Cliente: Pegaso Cliente S.L.");
    }

    /// <summary>«Empresa» vive en «Más filtros»; con ella aplicada, «Más filtros» se marca activo.</summary>
    [Fact]
    public void El_filtro_Empresa_de_Mas_filtros_filtra_la_consulta_y_la_url()
    {
        var mediador = ConDosClientes();
        var cut = Renderizar(mediador);
        Disparador(cut, "Más filtros").ClassList.Should().NotContain("menu-acciones-disparador-activa");

        Disparador(cut, "Más filtros").Click();
        cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Single(i => i.TextContent.Trim() == "Montajes Norte S.L.").Click();

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().EmpresaId.Should().Be(EmpresaMontajes);
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain($"empresa={EmpresaMontajes}");
        cut.AbrirGruposDeCentros();
        cut.FindAll(".lista-filas-acordeon .enlace-nombre-fila").Select(b => b.TextContent.Trim()).Should().Equal("Almacén Vigo", "Planta Murcia");
        Disparador(cut, "Más filtros").ClassList.Should().Contain("menu-acciones-disparador-activa");
        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Empresa: Montajes Norte S.L.");
    }

    [Theory]
    [InlineData("centros?empresa=no-es-un-guid", false)]
    [InlineData("centros?empresa=cccccccc-0000-0000-0000-000000000002", true)]
    public void Un_Id_de_empresa_de_la_url_solo_filtra_si_es_un_Id(string url, bool filtra)
    {
        var mediador = ConDosClientes();

        Renderizar(mediador, url: url);

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().EmpresaId.Should().Be(filtra ? EmpresaLimpiezas : null);
    }

    private static AngleSharp.Dom.IElement Disparador(IRenderedComponent<Centros> cut, string etiqueta) =>
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Single(p => p.GetAttribute("aria-label") == etiqueta);

    // ------------------------------------------------- revisión puente de la PR B

    /// <summary>
    /// Contraer el grupo de la fila enfocada suelta el foco: x no la marca para la baja en lote ni
    /// Enter abre su panel, porque ya no se ve.
    /// </summary>
    [Fact]
    public async Task Contraer_el_grupo_de_la_fila_enfocada_suelta_el_foco_y_x_no_la_marca()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        cut.FindAll(".grupo-lista-cabecera")[1].Click();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        cut.Find(".tarjeta-fila-acordeon.fila-enfocada").TextContent.Should().Contain("Planta Bilbao", "control: la fila quedó enfocada");

        cut.FindAll(".grupo-lista-cabecera")[1].Click();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("x"));
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("Enter"));

        workspace.EstaAbierto.Should().BeFalse("Enter no abre el panel de un Centro oculto");
        cut.FindAll(".grupo-lista-cabecera")[1].Click();
        cut.FindAll(".fila-enfocada").Should().BeEmpty("el foco se soltó al contraer");
        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();
        cut.FindAll(".tarjeta-fila-acordeon input[type=checkbox]").Should().OnlyContain(c => !c.HasAttribute("checked"),
            "x sobre una fila oculta no la marca para el lote");
    }

    /// <summary>
    /// Apagar la selección múltiple devuelve los grupos a su estado (contraídos): una fila enfocada
    /// mientras se veían abiertos deja de verse y suelta el foco.
    /// </summary>
    [Fact]
    public async Task Apagar_la_seleccion_multiple_suelta_el_foco_de_una_fila_que_deja_de_verse()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        var conmutador = "header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']";
        cut.Find(conmutador).Click();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        cut.FindAll(".fila-enfocada").Should().ContainSingle("control: hay fila enfocada con los grupos abiertos");

        cut.Find(conmutador).Click();
        cut.FindAll(".tarjeta-fila-acordeon").Should().BeEmpty("control: los grupos vuelven contraídos");
        cut.AbrirGruposDeCentros();

        cut.FindAll(".fila-enfocada").Should().BeEmpty();
    }

    /// <summary>
    /// Las opciones de «Empresa» salen de las Empresas de los Centros visibles, no del selector del alta
    /// (alcance de gestión), que para un usuario de portal va vacío aunque vea Centros con su Empresa.
    /// </summary>
    [Fact]
    public void Las_opciones_de_Empresa_salen_de_los_Centros_visibles_y_no_del_selector_de_gestion()
    {
        var mediador = ConDosClientes();
        mediador.EmpresasSelector.Should().BeEmpty("control: el selector de gestión no ofrece nada");
        var cut = Renderizar(mediador);

        Disparador(cut, "Más filtros").Click();

        cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Select(i => i.TextContent.Trim())
            .Should().Equal("Todas", "Montajes Norte S.L.", "Limpiezas Sur S.L.");
        mediador.Enviadas.OfType<ObtenerEmpresasParaSelectorQuery>().Should().BeEmpty("el filtro no usa el alcance de gestión");
    }

    /// <summary>Si las opciones de «Empresa» fallan, la pastilla «Cliente empresarial» sigue ofreciendo las suyas.</summary>
    [Fact]
    public void Un_fallo_en_las_opciones_de_Empresa_no_vacia_las_de_Cliente_empresarial()
    {
        var mediador = ConDosClientes();
        mediador.FallarEmpresasDeCentros = true;
        var cut = Renderizar(mediador);

        Disparador(cut, "Cliente").Click();

        cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Select(i => i.TextContent.Trim())
            .Should().Contain("Orion Cliente S.L.").And.Contain("Pegaso Cliente S.L.");
        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(3, "la lista se pinta igual");
    }

    /// <summary>
    /// Con un filtro llegado por la URL, su chip no dice «—» (que no existe) mientras las opciones aún
    /// no han llegado: dice «…», y el nombre en cuanto llegan.
    /// </summary>
    [Fact]
    public void El_chip_de_un_filtro_de_la_url_no_dice_que_no_existe_mientras_cargan_las_opciones()
    {
        var mediador = ConDosClientes();
        var retener = new TaskCompletionSource();
        mediador.RetenerEmpresasDeCentros = retener.Task;
        var cut = RenderizarConGruposContraidos(mediador, url: $"centros?empresa={EmpresaMontajes}");

        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Empresa: …");

        cut.InvokeAsync(() => retener.SetResult());

        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Empresa: Montajes Norte S.L."));
    }

    [Fact]
    public void Un_Id_de_la_url_que_no_esta_entre_las_opciones_cargadas_sale_como_raya()
    {
        var cut = Renderizar(ConDosClientes(), url: $"centros?empresa={Guid.NewGuid()}");

        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Empresa: —");
    }

    /// <summary>
    /// Pegaso (al día) aparece antes que Orion (con un vencido) en lo que devuelve la consulta: así se
    /// distingue el orden de grupos por peor estado del orden de primera aparición.
    /// </summary>
    private static Mediador ConPegasoPrimero()
    {
        var mediador = ConCentros(
            CentroDe("Planta Bilbao", ClientePegaso, EmpresaLimpiezas),
            CentroDe("Almacén Vigo", ClienteOrion, EmpresaMontajes, EstadoCentro.Vencido));
        mediador.ClientesSelector.AddRange([new(ClienteOrion, "Orion Cliente S.L."), new(ClientePegaso, "Pegaso Cliente S.L.")]);
        return mediador;
    }

    private static IEnumerable<string> NombresDeGrupo(IRenderedComponent<Centros> cut) =>
        cut.FindAll(".grupo-lista-nombre").Select(n => n.TextContent.Trim());

    /// <summary>Con el orden de serie los grupos salen del peor estado al mejor, como en la maqueta.</summary>
    [Fact]
    public void Con_el_orden_de_serie_los_grupos_van_del_peor_estado_al_mejor()
    {
        var cut = RenderizarConGruposContraidos(ConPegasoPrimero());

        NombresDeGrupo(cut).Should().Equal("Orion Cliente S.L.", "Pegaso Cliente S.L.");
    }

    /// <summary>
    /// Ordenar por cumplimiento sigue agrupando (orden y agrupación conviven, como en la maqueta), pero los
    /// grupos salen en el orden de su primera fila: el orden pedido no se contradice.
    /// </summary>
    [Fact]
    public void Ordenar_por_cumplimiento_sigue_agrupando_y_los_grupos_siguen_el_orden_pedido()
    {
        var mediador = ConPegasoPrimero();
        var cut = RenderizarConGruposContraidos(mediador);

        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").Click();

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().OrdenarPor.Should().Be(nameof(CentroListaDto.CumplimientoPorcentaje));
        cut.Find(".segmentado-lista button[aria-pressed=true]").TextContent.Trim().Should().Be("Por Cliente");
        NombresDeGrupo(cut).Should().Equal("Pegaso Cliente S.L.", "Orion Cliente S.L.");
    }

    [Fact]
    public void La_flecha_del_orden_no_entra_en_el_nombre_accesible()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").Click();

        var boton = cut.Find(".cabecera-columnas-centros .cabecera-columna-orden");
        boton.QuerySelector("span[aria-hidden=true]")!.TextContent.Should().Be("↑");
    }

    /// <summary>
    /// x activa la selección múltiple (como en la maqueta): la fila marcada queda a la vista con su casilla y
    /// los grupos no se pueden contraer mientras haya algo marcado, así que el lote no lleva Centros ocultos.
    /// </summary>
    [Fact]
    public async Task X_activa_la_seleccion_multiple_y_lo_marcado_no_se_puede_esconder()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        cut.FindAll(".grupo-lista-cabecera")[1].Click();
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));

        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("x"));

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").GetAttribute("aria-pressed").Should().Be("true");
        cut.FindAll("button.grupo-lista-cabecera").Should().BeEmpty("con algo marcado los grupos no se contraen");
        cut.FindAll(".tarjeta-fila-acordeon input[type=checkbox]").Should().HaveCount(3);
        cut.FindAll(".tarjeta-fila-acordeon input[type=checkbox][checked]").Should().ContainSingle();
    }

    /// <summary>
    /// Un Centro sin requisitos no tiene porcentaje: la barra pinta «—» y su nombre accesible no puede decir
    /// «% de cumplimiento» sin número (antes la etiqueta interpolaba el valor sin mirarlo).
    /// </summary>
    [Fact]
    public void Sin_requisitos_la_barra_del_Centro_no_anuncia_un_porcentaje()
    {
        var datos = ConDosClientes();
        datos.Centros[0] = datos.Centros[0] with { CumplimientoPorcentaje = null };

        var cut = Renderizar(datos);

        var etiquetas = cut.FindAll(".columna-cumplimiento-centro [data-pieza=barra-cumplimiento]")
            .Select(b => b.GetAttribute("aria-label")).ToList();
        etiquetas.Should().ContainSingle(e => e == "Sin requisitos");
        etiquetas.Where(e => e != "Sin requisitos").Should().OnlyContain(e => e!.StartsWith("87% de cumplimiento"));
    }

    /// <summary>
    /// Columnas de la maqueta aprobada: la Empresa tiene su columna y la barra de cumplimiento la suya, bajo
    /// sus rótulos; la segunda línea de la identidad es el código (agrupado, el Cliente empresarial ya lo dice
    /// la cabecera del grupo).
    /// </summary>
    [Fact]
    public void La_fila_lleva_las_columnas_Empresa_y_Cumplimiento_y_el_codigo_debajo_del_nombre()
    {
        var cut = Renderizar(ConDosClientes());

        var fila = cut.FindAll(".tarjeta-fila-acordeon").First(f => f.TextContent.Contains("Planta Bilbao"));
        fila.QuerySelector(".columna-empresa-centro")!.TextContent.Trim().Should().Be("Limpiezas Sur S.L.");
        fila.QuerySelector(".columna-cumplimiento-centro [data-pieza=barra-cumplimiento]")
            .Should().NotBeNull("la barra de cumplimiento vive en su columna");
        fila.QuerySelector(".tarjeta-fila-acordeon-cabecera [data-pieza=anillo]")
            .Should().BeNull("en los listados el cumplimiento es barra con cifra, no anillo");
        var meta = fila.QuerySelector(".tarjeta-fila-acordeon-meta")!;
        meta.ChildNodes.First().TextContent.Should().Be("C-001", "agrupado, la segunda línea es solo el código");
        meta.QuerySelector(".meta-empresa-movil")!.TextContent.Should().Be(" · Limpiezas Sur S.L.");
    }

    /// <summary>
    /// Cabecera y fila comparten las columnas de ancho propio en el mismo orden (con y sin selección
    /// múltiple): si no, cada valor deja de caer bajo su rótulo.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Cabecera_y_fila_llevan_las_mismas_columnas_en_el_mismo_orden(bool seleccionMultiple)
    {
        var cut = Renderizar(ConDosClientes());
        if (seleccionMultiple)
            cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();
        string[] columnas = ["columna-empresa-centro", "columna-cumplimiento-centro"];
        static IEnumerable<string> Orden(AngleSharp.Dom.IElement contenedor, string[] clases) =>
            contenedor.Children.SelectMany(c => c.ClassList).Where(clases.Contains);

        var cabecera = cut.Find(".cabecera-columnas-centros");
        var fila = cut.Find(".tarjeta-fila-acordeon-cabecera");

        Orden(fila, columnas).Should().Equal(columnas);
        Orden(cabecera, columnas).Should().Equal(Orden(fila, columnas));
        cabecera.Children.Length.Should().Be(fila.Children.Length, "una celda de cabecera por cada celda de la fila");
    }

    // ----------------------------- Agrupación y orden viajan en la URL (D4, T20)

    [Fact]
    public void Quitar_la_agrupacion_lo_escribe_en_la_url_y_volver_a_agrupar_lo_quita()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.FindAll(".segmentado-lista button").Single(b => b.TextContent.Trim() == "Sin agrupar").Click();

        navegacion.Uri.Should().Contain("agrupar=no");
        cut.Find(".segmentado-lista button[aria-pressed=true]").TextContent.Trim().Should().Be("Sin agrupar",
            "la pasada de parámetros que sigue a la navegación no puede devolver la agrupación");

        cut.FindAll(".segmentado-lista button").Single(b => b.TextContent.Trim() == "Por Cliente").Click();

        navegacion.Uri.Should().NotContain("agrupar", "agrupada es la vista de fábrica: no deja rastro en la URL");
    }

    [Fact]
    public void El_orden_por_cumplimiento_se_escribe_en_la_url_con_su_sentido()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").Click();
        navegacion.Uri.Should().EndWith("orden=cumplimiento");

        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").Click();
        navegacion.Uri.Should().EndWith("orden=cumplimiento-desc");
    }

    /// <summary>Recargar o compartir el enlace reproduce la vista: sin agrupar y con el orden pedido, en UNA consulta.</summary>
    [Fact]
    public void Un_enlace_con_agrupacion_y_orden_reproduce_la_vista_con_una_sola_consulta()
    {
        var mediador = ConDosClientes();

        var cut = RenderizarConGruposContraidos(mediador, "centros?agrupar=no&orden=cumplimiento-desc");

        cut.Find(".segmentado-lista button[aria-pressed=true]").TextContent.Trim().Should().Be("Sin agrupar");
        var consultas = mediador.Enviadas.OfType<ObtenerCentrosQuery>().ToList();
        consultas.Should().ContainSingle("la carga inicial ya lee el orden de la URL; la primera pasada de parámetros no la repite");
        (consultas[0].OrdenarPor, consultas[0].Descendente).Should().Be((nameof(CentroListaDto.CumplimientoPorcentaje), true));
    }

    [Fact]
    public void Un_orden_desconocido_en_la_url_deja_el_orden_de_catalogo()
    {
        var mediador = ConDosClientes();

        RenderizarConGruposContraidos(mediador, "centros?orden=nombre");

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().OrdenarPor.Should().BeNull();
    }

    [Fact]
    public void Sin_agrupar_la_segunda_linea_dice_tambien_el_Cliente_empresarial()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        cut.FindAll(".segmentado-lista button").Single(b => b.TextContent.Trim() == "Sin agrupar").Click();

        var fila = cut.FindAll(".tarjeta-fila-acordeon").First(f => f.TextContent.Contains("Planta Bilbao"));
        fila.QuerySelector(".tarjeta-fila-acordeon-meta")!.ChildNodes.First().TextContent.Should().Be("Pegaso Cliente S.L. · C-001");
    }

    /// <summary>
    /// El orden por cumplimiento se pide desde el rótulo de su columna (maqueta aprobada), no desde la barra de
    /// filtros; pulsarlo otra vez invierte el sentido.
    /// </summary>
    [Fact]
    public void El_rotulo_Cumplimiento_ordena_y_volver_a_pulsarlo_invierte_el_sentido()
    {
        var mediador = ConDosClientes();
        var cut = RenderizarConGruposContraidos(mediador);
        cut.FindAll(".barra-filtros-pastillas button").Should().NotContain(b => b.TextContent.Contains("Cumplimiento"),
            "el orden ya no vive en la barra de filtros");

        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").Click();
        var primera = mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last();
        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").Click();
        var segunda = mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last();

        (primera.OrdenarPor, primera.Descendente).Should().Be((nameof(CentroListaDto.CumplimientoPorcentaje), false));
        (segunda.OrdenarPor, segunda.Descendente).Should().Be((nameof(CentroListaDto.CumplimientoPorcentaje), true));
        cut.Find(".cabecera-columnas-centros .cabecera-columna-orden").GetAttribute("aria-pressed").Should().Be("true");
    }

    /// <summary>Con más de una página, los recuentos de grupo se rotulan como de esta página.</summary>
    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void Con_mas_de_una_pagina_avisa_de_que_los_grupos_son_de_esta_pagina(int centros, bool conNota)
    {
        var cut = RenderizarConGruposContraidos(ConCentros(Enumerable.Range(1, centros).Select(i => Centro($"Centro {i:00}")).ToArray()));

        cut.FindAll(".nota-grupos-pagina").Any().Should().Be(conNota);
        cut.Find(".grupo-lista-contador").GetAttribute("title").Should().EndWith("en esta página");
    }

    // ------------------------------------------------- agrupación por Cliente empresarial

    /// <summary>
    /// De serie, agrupada por Cliente empresarial y con los grupos contraídos: una cabecera por grupo
    /// con su contador y el resumen de la página, tintada con el peor estado del grupo.
    /// </summary>
    [Fact]
    public void De_serie_agrupa_por_Cliente_empresarial_con_los_grupos_contraidos_y_su_resumen()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        var grupos = cut.FindAll(".grupo-lista");
        grupos.Select(g => g.QuerySelector(".grupo-lista-nombre")!.TextContent.Trim()).Should().Equal("Orion Cliente S.L.", "Pegaso Cliente S.L.");
        grupos.Select(g => g.QuerySelector(".grupo-lista-contador")!.TextContent.Trim()).Should().Equal("2", "1");
        grupos.Select(g => g.QuerySelector(".grupo-lista-cabecera")!.GetAttribute("aria-expanded")).Should().Equal("false", "false");
        grupos[0].QuerySelector(".grupo-lista-resumen")!.TextContent.Should().Contain("1 con problema").And.Contain("1 por vencer");
        grupos[0].ClassList.Should().Contain("fila-tintada-peligro", "el peor de Orion está vencido");
        grupos[1].ClassList.Should().NotContain("fila-tintada-peligro").And.NotContain("fila-tintada-aviso");
        cut.FindAll(".tarjeta-fila-acordeon").Should().BeEmpty("los grupos contraídos no enseñan sus Centros");
        cut.Find(".segmentado-lista button[aria-pressed=true]").TextContent.Trim().Should().Be("Por Cliente");
    }

    [Fact]
    public void Abrir_un_grupo_muestra_solo_sus_Centros()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        cut.FindAll(".grupo-lista-cabecera")[1].Click();

        cut.FindAll(".lista-filas-acordeon .enlace-nombre-fila").Select(b => b.TextContent.Trim()).Should().Equal("Planta Bilbao");
        cut.FindAll(".grupo-lista-cabecera")[1].GetAttribute("aria-expanded").Should().Be("true");
    }

    /// <summary>
    /// Con búsqueda los grupos se ven abiertos y su cabecera no puede contraerlos: no se pinta como
    /// botón (antes era un botón con aria-expanded que, pulsado, no hacía nada).
    /// </summary>
    [Fact]
    public void Con_busqueda_los_grupos_se_ven_abiertos_y_su_cabecera_no_es_un_boton()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes(), url: "centros?q=planta");

        cut.FindAll(".grupo-lista-cabecera").Should().HaveCount(2);
        cut.FindAll("button.grupo-lista-cabecera").Should().BeEmpty();
        cut.FindAll(".grupo-lista-cabecera[aria-expanded]").Should().BeEmpty();
        cut.FindAll(".grupo-lista-cabecera-fija").Should().HaveCount(2);
        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(3);
    }

    [Fact]
    public void Sin_nada_que_los_fuerce_la_cabecera_de_grupo_es_un_boton()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        cut.FindAll("button.grupo-lista-cabecera[aria-expanded]").Should().HaveCount(2, "control positivo del caso forzado");
        cut.FindAll(".grupo-lista-cabecera-fija").Should().BeEmpty();
    }

    [Fact]
    public void Sin_agrupar_pinta_todas_las_filas_sin_cabeceras_de_grupo()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        cut.FindAll(".segmentado-lista button").Single(b => b.TextContent.Trim() == "Sin agrupar").Click();

        cut.FindAll(".grupo-lista").Should().BeEmpty();
        cut.FindAll(".lista-filas-acordeon .enlace-nombre-fila").Select(b => b.TextContent.Trim())
            .Should().Equal("Almacén Vigo", "Planta Murcia", "Planta Bilbao");
    }

    /// <summary>«Expandir todo» abre los grupos y el desplegable de cada fila; «Contraer todo» lo cierra todo.</summary>
    [Fact]
    public void Expandir_todo_abre_grupos_y_filas_y_Contraer_todo_lo_cierra()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        cut.Find(".barra-filtros-pastillas .barra-herramientas-lista button").Click();

        cut.FindAll(".grupo-lista-cabecera").Select(c => c.GetAttribute("aria-expanded")).Should().AllBe("true");
        cut.FindAll(".boton-expandir-fila").Select(b => b.GetAttribute("aria-expanded")).Should().Equal("true", "true", "true");
        cut.Find(".barra-filtros-pastillas .barra-herramientas-lista button").TextContent.Trim().Should().Be("Contraer todo");

        cut.Find(".barra-filtros-pastillas .barra-herramientas-lista button").Click();

        cut.FindAll(".grupo-lista-cabecera").Select(c => c.GetAttribute("aria-expanded")).Should().AllBe("false");
        cut.FindAll(".tarjeta-fila-acordeon").Should().BeEmpty();
    }

    /// <summary>
    /// Con la selección múltiple activa los grupos se ven abiertos: «Seleccionar los de esta página»
    /// y la barra de lote actúan sobre filas que el usuario ve, nunca sobre filas escondidas.
    /// </summary>
    [Fact]
    public void Con_seleccion_multiple_los_grupos_se_ven_abiertos()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();

        cut.FindAll(".tarjeta-fila-acordeon input[type=checkbox]").Should().HaveCount(3);
    }

    /// <summary>Filas con problema: vencido, falta documentación o bloqueo → peligro; urgente → aviso; el resto, sin tinte.</summary>
    [Theory]
    [InlineData(EstadoCentro.Vencido, "fila-tintada-peligro")]
    [InlineData(EstadoCentro.Faltante, "fila-tintada-peligro")]
    [InlineData(EstadoCentro.Bloqueado, "fila-tintada-peligro")]
    [InlineData(EstadoCentro.Urgente, "fila-tintada-aviso")]
    [InlineData(EstadoCentro.Proximo, null)]
    [InlineData(EstadoCentro.Vigente, null)]
    public void Las_filas_con_problema_van_tintadas_por_su_estado(EstadoCentro estado, string? tinte)
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte", estado)));

        var clases = cut.Find(".tarjeta-fila-acordeon").ClassList;
        clases.Where(c => c.StartsWith("fila-tintada-", StringComparison.Ordinal)).Should().Equal(tinte is null ? [] : [tinte]);
    }

    /// <summary>j/k recorren las filas que se ven, en el orden en que se pintan: un grupo contraído no cuenta.</summary>
    [Fact]
    public void J_recorre_solo_las_filas_visibles()
    {
        var cut = RenderizarConGruposContraidos(ConDosClientes());
        cut.FindAll(".grupo-lista-cabecera")[1].Click();

        var atajos = cut.FindComponent<AtajosListaTeclado>();
        cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));

        cut.Find(".tarjeta-fila-acordeon.fila-enfocada").TextContent.Should().Contain("Planta Bilbao");
    }

    [Fact]
    public void La_lista_tiene_una_cabecera_de_columnas_sin_roles_de_tabla()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        var cabecera = cut.Find(".cabecera-columnas-centros");
        cabecera.TextContent.Should().Contain("Centro").And.Contain("Empresa").And.Contain("Cumplimiento")
            .And.Contain("Estado").And.Contain("Visita");
        // Vencidos y próximos no tienen columna: son el motivo bajo la pastilla de estado.
        cabecera.TextContent.Should().NotContain("Venc.").And.NotContain("Próx.");
        cabecera.QuerySelectorAll(".cabecera-columnas-centros-indicadores > span").Select(s => s.TextContent.Trim())
            .Should().Equal("Estado", "Visita");
        cabecera.GetAttribute("role").Should().BeNull("no es una tabla: la fila se despliega");
        cut.Markup.IndexOf("cabecera-columnas-centros", StringComparison.Ordinal)
            .Should().BeLessThan(cut.Markup.IndexOf("lista-filas-acordeon", StringComparison.Ordinal));
    }

    /// <summary>
    /// Un clic en cualquier punto de la fila abre la vista rápida. Se pulsa una celda sin controles (la
    /// de la Empresa): el clic sube a la fila, que es quien lo atiende.
    /// </summary>
    [Fact]
    public async Task Un_clic_en_la_fila_abre_la_vista_rapida_del_centro()
    {
        var centro = Centro("Centro Norte");
        var cut = Renderizar(ConCentros(centro));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        cut.Find(".tarjeta-fila-acordeon-cabecera").ClassList.Should().Contain("fila-pulsable");

        await cut.Find(".tarjeta-fila-acordeon-cabecera .columna-empresa-centro").ClickAsync(new MouseEventArgs());

        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Centro, centro.Id, "Centro Norte", "informacion"));
    }

    /// <summary>
    /// La casilla (que reacciona al cambio, no al clic) y el icono 360 (que navega el navegador) no
    /// tienen manejador de clic propio y cortan la subida: bUnit lo dice con «nadie recibe este clic».
    /// Sin el corte, el clic llegaría a la fila —que sí lo atiende— y no habría excepción. El corte del
    /// desplegable, que tiene manejador propio, solo lo prueba el E2E
    /// (<c>CentrosFilaSinMenuE2ETests</c>): bUnit queda en verde aunque se quite.
    /// Por lo mismo, el de la pastilla de visitas no tiene prueba: bUnit no lo ve y la siembra del E2E
    /// no garantiza un Centro con visita.
    /// </summary>
    [Theory]
    [InlineData("input[aria-label='Seleccionar el centro Centro Norte']")]
    [InlineData("a.boton-360-pagina")]
    public async Task La_casilla_y_el_icono_360_cortan_el_clic_antes_de_la_fila(string selector)
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));
        await cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());

        var clic = () => cut.Find(".lista-filas-acordeon " + selector).ClickAsync(new MouseEventArgs());

        await clic.Should().ThrowAsync<MissingEventHandlerException>();
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull();
    }

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición. La petición queda en el servicio
    /// para que el panel la atienda (y solo para esa ficha).
    /// </summary>
    [Fact]
    public async Task La_tecla_e_abre_la_vista_rapida_de_la_fila_enfocada_pidiendo_edicion()
    {
        var centro = Centro("Centro Norte");
        var cut = Renderizar(ConCentros(centro));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        await cut.InvokeAsync(() => atajos.RecibirAtajo("e"));
        workspace.FrameActual.Should().BeNull("sin fila enfocada ni panel abierto, «e» no tiene qué editar");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("e"));

        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Centro, centro.Id, "Centro Norte", "informacion"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Centro, centro.Id).Should().BeTrue("la edición quedó pedida para esa ficha");
    }

    /// <summary>
    /// Sin fila enfocada, «e» edita la ficha que esté abierta, aunque su Centro no esté en la página (el
    /// filtro lo dejó fuera o la lista está vacía): el nombre sale del frame abierto.
    /// </summary>
    [Fact]
    public async Task La_tecla_e_sin_fila_enfocada_edita_la_ficha_abierta_aunque_no_este_en_la_lista()
    {
        var fueraDeLaLista = Guid.NewGuid();
        var cut = Renderizar(ConCentros());
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Centro, fueraDeLaLista, "Planta Bilbao", "requisitos"));

        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo("e"));

        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Centro, fueraDeLaLista, "Planta Bilbao", "informacion"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Centro, fueraDeLaLista).Should().BeTrue();
    }

    /// <summary>
    /// Una fila enfocada que quedó dentro de un grupo contraído no se ve: «e» no la edita a ciegas (misma
    /// regla que «x» y Enter).
    /// </summary>
    [Fact]
    public async Task La_tecla_e_no_edita_una_fila_enfocada_que_quedo_en_un_grupo_contraido()
    {
        var centro = Centro("Centro Norte");
        var cut = Renderizar(ConCentros(centro));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        cut.Find(".fila-enfocada .enlace-nombre-fila").TextContent.Trim().Should().Be("Centro Norte", "control positivo");

        await cut.Find("button.grupo-lista-cabecera").ClickAsync(new MouseEventArgs());
        cut.FindAll(".tarjeta-fila-acordeon").Should().BeEmpty("control positivo: el grupo se contrajo");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("e"));

        workspace.FrameActual.Should().BeNull();
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Centro, centro.Id).Should().BeFalse();
    }

    [Fact]
    public async Task El_nombre_de_la_fila_abre_el_panel_del_centro_y_no_un_segundo_drawer()
    {
        // Pieza 6: en Centros la vista previa ES el panel del Context Workspace, que ya existe; el contrato
        // prohíbe sumarle un PreviewDrawer.
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        workspace.EstaAbierto.Should().BeFalse("control: cerrado al inicio");
        await cut.Find(".tarjeta-fila-acordeon-identidad .enlace-nombre-fila").ClickAsync(new MouseEventArgs());

        workspace.EstaAbierto.Should().BeTrue("el nombre abre el panel del centro");
        cut.FindAll("[class*='drawer-preview']").Should().BeEmpty("no hay un segundo drawer de vista previa");
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void El_paginador_solo_aparece_con_mas_de_una_pagina(int centros, bool conPaginador)
    {
        var cut = Renderizar(ConCentros(Enumerable.Range(1, centros).Select(i => Centro($"Centro {i:00}")).ToArray()));

        cut.FindAll(".paginador").Any().Should().Be(conPaginador);
    }

    [Fact]
    public void El_paginador_ofrece_Mostrar_N_y_cambiarlo_vuelve_a_pedir_con_ese_tamano()
    {
        var mediador = ConCentros(Enumerable.Range(1, 21).Select(i => Centro($"Centro {i:00}")).ToArray());
        var cut = Renderizar(mediador);

        cut.Find(".paginador-tamano-select").Change("50");

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().TamanoPagina.Should().Be(50);
    }
}
