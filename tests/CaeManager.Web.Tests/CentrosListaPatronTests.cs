using Bunit;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
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
/// Lote 3 del selector de Tenant y patrón único de lista en <c>/centros</c>: la cabecera con la
/// empresa gestionada activa, el estado 4a, la carga asíncrona de la empresa, y las piezas del patrón
/// (cabecera, BarraFiltros con chips, herramientas, cabecera de columnas, «⋯», vista previa y pie).
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

        /// <summary>Por defecto, un usuario mono-Tenant: sin selector ni cabecera de empresa gestionada.</summary>
        public List<ClienteAutorizadoDto> Autorizados { get; } = [new(Guid.NewGuid(), "Propia", EsOrigen: true)];

        /// <summary>Si se fija, la respuesta de la lista de Tenants autorizados espera a esta tarea (mediador asíncrono).</summary>
        public Task? RetenerAutorizados { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerClientesAutorizadosQuery && RetenerAutorizados is { } espera)
                await espera;

            return (TResponse)(request switch
            {
                ObtenerClientesAutorizadosQuery => (object)(IReadOnlyList<ClienteAutorizadoDto>)Autorizados.ToList(),
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(Centros, Centros.Count, q.Pagina, q.TamanoPagina),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)Visitas,
                ObtenerClientesParaSelectorQuery => Array.Empty<ClienteSelectorDto>(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });
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

    private IRenderedComponent<Centros> Renderizar(Mediador mediador, string url = "centros", Guid? tenantSeleccionado = null)
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

    [Fact]
    public void Con_varias_empresas_gestionadas_la_cabecera_de_la_lista_dice_cual_esta_activa()
    {
        var cut = Renderizar(ConCartera(origenGestionado: false, Centro("Centro Norte")), tenantSeleccionado: EmpresaSur);

        var cabecera = cut.Find(".cabecera-empresa-activa");
        cabecera.TextContent.Should().Contain("Empresa gestionada").And.Contain("Empresa Sur");
        cabecera.QuerySelector(".avatar-tenant")!.TextContent.Trim().Should().Be("ES", "sin logo se pintan las iniciales");
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
        cut.FindAll(".barra-filtros-lista").Should().BeEmpty();
        cut.FindAll("a.enlace-exportar").Should().BeEmpty("exportaría los datos del origen");
        cut.Markup.Should().NotContain("+ Nuevo centro").And.NotContain("Centro del origen");
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty("no se piden los centros de la organización de origen");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var cut = Renderizar(ConCartera(origenGestionado: true, Centro("Centro del origen")));

        cut.Markup.Should().NotContain("Selecciona una empresa de tu cartera");
        cut.Find(".cabecera-empresa-activa").TextContent.Should().Contain("Operador de prueba");
        cut.Markup.Should().Contain("Centro del origen");
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista_ni_se_ofrece_la_exportacion()
    {
        var puerta = new TaskCompletionSource();
        var mediador = ConCartera(origenGestionado: false, Centro("Centro Sur"));
        mediador.RetenerAutorizados = puerta.Task;

        var cut = Renderizar(mediador, tenantSeleccionado: EmpresaSur);

        cut.FindAll(".barra-filtros-lista").Should().BeEmpty();
        cut.FindAll("a.enlace-exportar").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().BeEmpty();

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-lista").Should().NotBeEmpty());
        cut.FindAll("a.enlace-exportar").Should().HaveCount(2);
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

    [Fact]
    public void La_cabecera_lleva_el_antetitulo_las_exportaciones_y_una_sola_primaria()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        cut.Find("header.cabecera-pagina").TextContent.Should().Contain("Negocio");
        var acciones = cut.Find(".cabecera-pagina .acciones-cabecera");
        acciones.TextContent.Should().Contain("Exportar a Excel").And.Contain("Exportar asignaciones").And.Contain("+ Nuevo centro");
        acciones.QuerySelectorAll("button").Should().ContainSingle("una sola acción primaria");
    }

    [Fact]
    public void Ninguna_accion_de_la_pagina_queda_dentro_de_la_barra_de_filtros()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        cut.FindAll(".barra-filtros-lista a").Should().BeEmpty();
        cut.FindAll(".barra-filtros-lista button").Should().BeEmpty("sin filtros activos solo hay buscador y selects");
        cut.FindAll(".barra-trabajo-centros").Should().BeEmpty("la fila única de acciones ya no existe");
    }

    [Fact]
    public void Los_filtros_llevan_su_etiqueta_visible_encima()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        cut.FindAll(".barra-filtros-lista label").Select(l => l.TextContent.Trim()).Should().Contain("Estado");
    }

    [Fact]
    public void Los_filtros_activos_salen_como_chips_y_cada_chip_quita_su_filtro_de_la_url()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")), url: "centros?q=norte&estado=Vencido");

        var chips = cut.FindAll(".barra-filtros-lista .chip-filtro").Select(c => c.TextContent.Trim()).ToList();
        chips.Should().HaveCount(2);
        chips.Should().Contain(c => c.Contains("norte")).And.Contain(c => c.Contains("Vencido"));

        cut.FindAll(".barra-filtros-lista .chip-filtro")
            .Single(c => c.TextContent.Contains("Vencido")).QuerySelector(".chip-filtro-quitar")!.Click();

        var url = Services.GetRequiredService<NavigationManager>().Uri;
        url.Should().NotContain("estado=").And.Contain("q=norte");
        cut.FindAll(".barra-filtros-lista .chip-filtro").Should().ContainSingle();
    }

    [Fact]
    public void Limpiar_todo_quita_los_dos_filtros_de_la_url_en_una_sola_navegacion()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")), url: "centros?q=norte&estado=Vencido");

        cut.Find(".limpiar-filtros-barra").Click();

        var url = Services.GetRequiredService<NavigationManager>().Uri;
        url.Should().NotContain("q=").And.NotContain("estado=");
        cut.FindAll(".barra-filtros-lista .chip-filtro").Should().BeEmpty();
    }

    [Fact]
    public void La_barra_de_herramientas_lleva_seleccion_multiple_expandir_todos_y_el_recuento_en_ese_orden()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte"), Centro("Centro Sur")), url: "centros?estado=Vencido");

        var texto = cut.Find(".barra-herramientas-lista").TextContent;
        var seleccion = texto.IndexOf("Selección múltiple", StringComparison.Ordinal);
        var expandir = texto.IndexOf("Expandir todos", StringComparison.Ordinal);
        var recuento = texto.IndexOf("2 centros con estos filtros", StringComparison.Ordinal);
        seleccion.Should().BeGreaterThanOrEqualTo(0);
        expandir.Should().BeGreaterThan(seleccion);
        recuento.Should().BeGreaterThan(expandir);
    }

    [Fact]
    public void La_lista_tiene_una_cabecera_de_columnas_sin_roles_de_tabla()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        var cabecera = cut.Find(".cabecera-columnas-centros");
        cabecera.TextContent.Should().Contain("Centro").And.Contain("Venc.").And.Contain("Próx.").And.Contain("Estado / visita");
        cabecera.GetAttribute("role").Should().BeNull("no es una tabla: la fila se despliega");
        cut.Markup.IndexOf("cabecera-columnas-centros", StringComparison.Ordinal)
            .Should().BeLessThan(cut.Markup.IndexOf("lista-filas-acordeon", StringComparison.Ordinal));
    }

    [Fact]
    public async Task La_ultima_columna_es_el_menu_de_tres_puntos_y_no_el_enlace_Detalles()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));

        cut.FindAll(".tarjeta-fila-acordeon-acciones button").Select(b => b.TextContent.Trim()).Should().NotContain("Detalles");
        await cut.Find(".tarjeta-fila-acordeon-acciones .menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        var items = cut.FindAll(".menu-acciones-item").Select(i => i.TextContent.Trim()).ToList();
        items.Should().Equal("Ver ficha 360", "Vista previa");
    }

    [Fact]
    public async Task Ver_ficha_360_del_menu_lleva_a_la_pagina_del_centro()
    {
        var centro = Centro("Centro Norte");
        var cut = Renderizar(ConCentros(centro));

        await cut.Find(".tarjeta-fila-acordeon-acciones .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Ver ficha 360").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/centros/{centro.Id}");
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

    [Fact]
    public async Task Vista_previa_del_menu_de_fila_abre_el_mismo_panel()
    {
        var cut = Renderizar(ConCentros(Centro("Centro Norte")));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await cut.Find(".tarjeta-fila-acordeon-acciones .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Vista previa").ClickAsync(new MouseEventArgs());

        workspace.EstaAbierto.Should().BeTrue();
    }

    [Fact]
    public void El_paginador_ofrece_Mostrar_N_y_cambiarlo_vuelve_a_pedir_con_ese_tamano()
    {
        var mediador = ConCentros(Centro("Centro Norte"));
        var cut = Renderizar(mediador);

        cut.Find(".paginador-tamano-select").Change("50");

        mediador.Enviadas.OfType<ObtenerCentrosQuery>().Last().TamanoPagina.Should().Be(50);
    }
}
