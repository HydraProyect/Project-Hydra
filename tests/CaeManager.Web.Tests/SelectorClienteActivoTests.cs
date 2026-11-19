using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Marco.Queries.ObtenerContextoMarco;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Selector de empresa gestionada (Tenant beneficiario) de la cabecera de la barra
/// lateral: mockup «Selector Empresa Gestionada» y decisiones 1, 7 bis y 7 ter del
/// contrato. Lo que se prueba es lo que decide la pantalla —cuándo aparece, qué
/// lista, qué marca como activo, a dónde vuelve—; que el POST autorice o no lo
/// prueba la capa de integración con RLS, no esta.
/// </summary>
public class SelectorClienteActivoTests : BunitContext
{
    private static readonly Guid Origen = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Norte = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid Sur = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000003");

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class Mediador(
        Func<IReadOnlyList<ClienteAutorizadoDto>> lista, Func<Task<MiTrabajoAgregadoDto>> miTrabajo, Action alCalcularMiTrabajo,
        Func<bool> participaEnIncorporacion, Action alPedirContextoMarco,
        Func<IReadOnlyList<ClienteAutorizadoDto>?> listaPorPapel) : IMediator
    {
        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default)
        {
            // El contexto del marco se compone con el handler real sobre las respuestas de abajo; cuántas veces se pide
            // es lo que miden las pruebas de reutilización.
            if (request is ObtenerContextoMarcoQuery)
            {
                alPedirContextoMarco();
                return (T)(object)await ContextoMarcoDePrueba.ComponerAsync(this, cancellationToken);
            }
            if (request is ObtenerClientesAutorizadosQuery { PorPapel: true } && listaPorPapel() is { } porPapel) return (T)(object)porPapel;
            if (request is ObtenerClientesAutorizadosQuery) return (T)(object)lista();
            if (request is CaeManager.Application.Operaciones.IncorporacionCartera.Queries.ParticipaEnIncorporacionCarteraQuery)
                return (T)(object)participaEnIncorporacion();
            if (request is ObtenerMiTrabajoAgregadoQuery)
            {
                alCalcularMiTrabajo();
                return (T)(object)await miTrabajo();
            }

            throw new NotSupportedException(request.GetType().Name);
        }

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioFalso(Func<PapelActivo>? papel = null, Func<string>? rol = null) : ICurrentUserService
    {
        public Task<PapelActivo> ObtenerPapelActivoAsync() => Task.FromResult(papel?.Invoke() ?? PapelActivo.SinPapel);
        public static readonly Guid Id = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Id);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol?.Invoke() ?? "GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class PapelesRecordadosFalsos(Func<IReadOnlyDictionary<Guid, ServicioCae>> papeles) : ILectorPapelRecordadoSelectorTenant
    {
        public IReadOnlyDictionary<Guid, ServicioCae> Leer(Guid usuarioId) =>
            usuarioId == UsuarioFalso.Id ? papeles() : new Dictionary<Guid, ServicioCae>();
    }

    private sealed class RecientesFalsos(Func<IReadOnlyList<Guid>> lista) : ILectorRecientesSelectorTenant
    {
        public IReadOnlyList<Guid> Leer(Guid usuarioId) => usuarioId == UsuarioFalso.Id ? lista() : [];
    }

    private IReadOnlyList<ClienteAutorizadoDto> _lista = [];
    private IReadOnlyList<Guid> _recientes = [];
    private IReadOnlyList<ClienteAutorizadoDto>? _listaPorPapel;
    private PapelActivo _papel = PapelActivo.SinPapel;
    private string _rol = "GestorCae";
    private IReadOnlyDictionary<Guid, ServicioCae> _papelesRecordados = new Dictionary<Guid, ServicioCae>();
    private Func<Task<MiTrabajoAgregadoDto>> _miTrabajo = () => Task.FromResult(new MiTrabajoAgregadoDto([]));
    private int _cuantasVecesSeCalculoMiTrabajo;
    private bool _participaEnIncorporacion;
    private int _contextosPedidos;
    private readonly BunitPersistentComponentState _estadoPersistente;

    public SelectorClienteActivoTests()
    {
        _estadoPersistente = AddBunitPersistentComponentState();
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new Mediador(() => _lista, () => _miTrabajo(), () => _cuantasVecesSeCalculoMiTrabajo++,
            () => _participaEnIncorporacion, () => _contextosPedidos++, () => _listaPorPapel));
        Services.AddScoped<ContextoMarcoDelAmbito>();
        Services.AddScoped<CaeManager.Web.Components.DesignSystem.ToastService>();
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioFalso(() => _papel, () => _rol));
        Services.AddScoped<ILectorPapelRecordadoSelectorTenant>(_ => new PapelesRecordadosFalsos(() => _papelesRecordados));
        // El Tenant efectivo es el seleccionado, como en la composición real: entra en la clave de la presentación.
        Services.AddScoped<ITenantActual>(sp =>
            sp.GetRequiredService<IClienteActivoSeleccionado>() as ITenantActual ?? new SeleccionEmpresaGestionadaDePrueba());
        Services.AddSingleton<IContadorPendientesSelectorTenant>(new ContadorPendientesSelectorTenant(TimeProvider.System));
        Services.AddScoped<ILectorRecientesSelectorTenant>(_ => new RecientesFalsos(() => _recientes));
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ExcepcionDeCircuitoDesconectado>>(
            NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);
    }

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    [Fact]
    public void La_presentacion_prerenderizada_se_lee_una_vez_por_ambito_y_se_persiste_para_el_circuito()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var prerender = Renderizar(Norte);
        _estadoPersistente.TriggerOnPersisting();
        prerender.Dispose();
        _lista = [];

        // bUnit comparte un solo ámbito: el segundo render es el mismo ámbito y reutiliza lo leído sin despachar.
        var interactivo = Render<SelectorClienteActivo>();

        interactivo.Find(".selector-tenant-disparador").TextContent.Should().Contain("Empresa Norte");
        interactivo.Markup.Should().Contain("token-de-prueba", "el token antiforgery se obtiene en la instancia interactiva");
        _contextosPedidos.Should().Be(1, "el contexto del marco se lee una sola vez en el ámbito");
        _estadoPersistente.TryTake<ContextoMarcoDelAmbito.ContextoPersistido>(ContextoMarcoDelAmbito.ClavePersistencia, out var persistido)
            .Should().BeTrue("lo leído en el prerenderizado viaja al circuito");
        persistido!.Contexto.Seleccionado.Should().Be(Norte);
        persistido.Contexto.Clave.UsuarioId.Should().Be(UsuarioFalso.Id);
        persistido.Contexto.Clave.TenantOrigenId.Should().Be(Origen);
        persistido.Marco.ClientesAutorizados.Select(c => c.Nombre).Should().Contain("Empresa Norte");
    }

    [Theory]
    [InlineData("vigente")]
    [InlineData("caducada")]
    [InlineData("otro-seleccionado")]
    [InlineData("otro-usuario")]
    [InlineData("otro-origen")]
    [InlineData("otro-perfil")]
    [InlineData("otro-tenant-efectivo")]
    [InlineData("otro-ambito")]
    public void Solo_se_reutiliza_la_presentacion_vigente_del_mismo_contexto(string escenario)
    {
        Services.AddScoped<IClienteActivoSeleccionado>(_ => new SeleccionEmpresaGestionadaDePrueba(Norte));
        var clave = new ClaveContextoMemo(
            escenario == "otro-usuario" ? Guid.NewGuid() : UsuarioFalso.Id,
            escenario == "otro-origen" ? Guid.NewGuid() : Origen,
            escenario == "otro-perfil" ? "Consulta" : "GestorCae",
            escenario == "otro-tenant-efectivo" ? Sur : Norte,
            escenario == "otro-ambito" ? Norte : null);
        _estadoPersistente.Persist(ContextoMarcoDelAmbito.ClavePersistencia,
            new ContextoMarcoDelAmbito.ContextoPersistido(
                new(clave, escenario == "otro-seleccionado" ? Sur : Norte),
                DateTimeOffset.UtcNow - (escenario == "caducada" ? TimeSpan.FromMinutes(1) : TimeSpan.Zero),
                new ContextoMarcoDto(default, RotulosPrimeraPersona: false, EsAdministradorPlataforma: false,
                    [Propio(), Gestionado(Norte, "Dato antiguo"), Gestionado(Sur, "Empresa Sur")],
                    OrdenGrupos: null, OrdenEnlaces: null, ParticipaEnIncorporacionCartera: true, Avatar: null)));
        _lista = [Propio(), Gestionado(Norte, "Dato vigente"), Gestionado(Sur, "Empresa Sur")];

        var cut = Render<SelectorClienteActivo>();

        cut.Find(".selector-tenant-disparador").TextContent.Should()
            .Contain(escenario == "vigente" ? "Dato antiguo" : "Dato vigente");
    }

    [Fact]
    public void Una_cartera_grande_no_se_persiste_para_el_circuito()
    {
        _lista = [Propio(), .. Enumerable.Range(0, 30).Select(i => Gestionado(Guid.NewGuid(), $"Empresa {i}"))];
        var prerender = Renderizar();
        _estadoPersistente.TriggerOnPersisting();
        prerender.Dispose();

        _estadoPersistente.TryTake<ContextoMarcoDelAmbito.ContextoPersistido>(
                ContextoMarcoDelAmbito.ClavePersistencia, out _)
            .Should().BeFalse("el estado persistido vuelve al servidor en el mensaje de arranque del circuito");
    }

    private static ClienteAutorizadoDto Gestionado(Guid id, string nombre) =>
        new(id, nombre, EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);

    private IRenderedComponent<SelectorClienteActivo> Renderizar(Guid? seleccionado = null, string url = "trabajadores")
    {
        Services.AddScoped<IClienteActivoSeleccionado>(_ => new SeleccionEmpresaGestionadaDePrueba(seleccionado));
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        return Render<SelectorClienteActivo>();
    }

    private static async Task Abrir(IRenderedComponent<SelectorClienteActivo> cut) =>
        await cut.Find(".selector-tenant-disparador").ClickAsync(new MouseEventArgs());

    private static List<string> NombresDeLaLista(IRenderedComponent<SelectorClienteActivo> cut) =>
        cut.FindAll(".selector-tenant-lista .selector-tenant-fila .selector-tenant-nombre").Select(n => n.TextContent.Trim()).ToList();

    // --- Visibilidad (decisión 1, evaluada por condición) ---------------------------------------

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_el_selector()
    {
        _lista = [Propio()];

        var cut = Renderizar();

        cut.FindAll(".selector-tenant").Should().BeEmpty();
        cut.Markup.Should().NotContain("Empresa gestionada");
    }

    [Fact]
    public void Con_un_unico_Tenant_de_cartera_y_el_origen_sin_gestionar_el_selector_sigue_oculto()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];

        Renderizar(seleccionado: Norte).FindAll(".selector-tenant").Should().BeEmpty();
    }

    [Fact]
    public void Decision_7_quater_con_un_unico_Tenant_de_cartera_de_otro_rol_el_selector_se_ve_tambien_dentro_del_Tenant()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte") with { EsCarteraGestorCae = false }];

        Renderizar(seleccionado: Norte).FindAll(".selector-tenant").Should().NotBeEmpty(
            "es el único control del Operador delegado para volver a su Tenant de origen");
    }

    [Fact]
    public void Con_varias_empresas_gestionadas_el_selector_es_visible_y_dice_cual_esta_activa()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];

        var cut = Renderizar(seleccionado: Sur);

        var disparador = cut.Find(".selector-tenant-disparador");
        disparador.GetAttribute("data-tenant-id").Should().Be(Sur.ToString());
        disparador.GetAttribute("aria-label").Should().Be("Contexto operativo actual: Empresa Sur");
        disparador.TextContent.Should().Contain("Empresa gestionada").And.Contain("Empresa Sur");
        disparador.GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".selector-tenant-panel").Should().BeEmpty("la lista está cerrada hasta pulsar el disparador");
    }

    [Fact]
    public void Sin_seleccion_el_activo_es_el_Tenant_de_origen()
    {
        _lista = [Propio(gestionado: true), Gestionado(Norte, "Empresa Norte")];

        Renderizar().Find(".selector-tenant-disparador").GetAttribute("data-tenant-id").Should().Be(Origen.ToString());
    }

    [Fact]
    public void En_el_propio_Tenant_del_Operador_CAE_el_boton_dice_Tu_organizacion_y_no_Empresa_gestionada()
    {
        _lista = [Propio(gestionado: true), Gestionado(Norte, "Empresa Norte")];

        var disparador = Renderizar().Find(".selector-tenant-disparador");

        disparador.TextContent.Should().Contain("Tu organización").And.NotContain("Empresa gestionada");
    }

    [Fact]
    public void Quien_solo_alcanza_un_Tenant_por_la_via_heredada_conserva_el_selector()
    {
        _lista = [Propio(), new ClienteAutorizadoDto(Sur, "Delegante", EsOrigen: false)];

        Renderizar().FindAll(".selector-tenant").Should().ContainSingle();
    }

    // --- Lista ---------------------------------------------------------------------------------

    [Fact]
    public async Task Al_abrir_muestra_la_cartera_con_su_cuenta_el_activo_marcado_y_el_origen_fijo_debajo()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte);

        await Abrir(cut);

        cut.Find(".selector-tenant-cabecera").TextContent.Should().Be("Empresas gestionadas · 2");
        NombresDeLaLista(cut).Should().Equal("Empresa Norte", "Empresa Sur");
        var opciones = cut.FindAll("[role=option]");
        opciones.Should().HaveCount(3, "dos de la cartera y «Tu organización» debajo");
        opciones.Single(o => o.GetAttribute("aria-selected") == "true").GetAttribute("value").Should().Be(Norte.ToString());
        var origen = opciones.Last();
        origen.GetAttribute("value").Should().Be(Origen.ToString());
        origen.TextContent.Should().Contain("Tu organización");
        cut.Find(".selector-tenant-disparador").GetAttribute("aria-expanded").Should().Be("true");
    }

    [Fact]
    public async Task Un_Tenant_cuya_Asignacion_caduco_deja_de_listarse_y_la_seleccion_vuelve_al_origen()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Sur);
        await Abrir(cut);
        NombresDeLaLista(cut).Should().Contain("Empresa Sur");

        // La query ya no devuelve el Tenant caducado: otra pantalla nueva del selector.
        // Otra pantalla es otra petición y otro ámbito; bUnit comparte uno solo, así que se descarta lo leído.
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];
        Services.GetRequiredService<ContextoMarcoDelAmbito>().Invalidar();
        var refrescado = Render<SelectorClienteActivo>();

        // Su selección (Sur) ya no está autorizada: el contexto efectivo vuelve al origen y, como aún
        // alcanza a Norte, el control se mantiene para que pueda elegirlo.
        refrescado.Find(".selector-tenant-disparador").GetAttribute("data-tenant-id").Should().Be(Origen.ToString());
        await refrescado.Find(".selector-tenant-disparador").ClickAsync(new MouseEventArgs());
        refrescado.Markup.Should().Contain("Empresa Norte").And.NotContain("Empresa Sur");
    }

    [Fact]
    public void Con_un_unico_Tenant_de_cartera_el_selector_reaparece_si_el_contexto_efectivo_es_el_origen()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];

        Renderizar(seleccionado: null).FindAll(".selector-tenant").Should().ContainSingle(
            "volvió al origen a propósito y la preferencia impide el Tenant por defecto");
    }

    [Fact]
    public async Task Cada_opcion_envia_su_Tenant_por_POST_al_endpoint_con_antiforgery_y_vuelve_a_la_ruta_sin_query()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(url: "trabajadores?q=ana&estado=Vencido&empresaId=1234");

        await Abrir(cut);

        // Cada opción es su propio formulario (D-4: así una entrada con papel puede enviar también su servicio).
        var formularios = cut.FindAll(".selector-tenant-panel form");
        formularios.Should().HaveCount(3);
        foreach (var formulario in formularios)
        {
            formulario.GetAttribute("method").Should().Be("post");
            formulario.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
            formulario.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value").Should().Be("token-de-prueba");
            formulario.QuerySelector("input[name=returnUrl]")!.GetAttribute("value").Should().Be("/trabajadores",
                "los filtros con Ids del Tenant anterior no sobreviven al cambio (I14)");
            formulario.QuerySelector("input[name=servicio]").Should().BeNull("una entrada sin papel no envía servicio");
            formulario.QuerySelectorAll("button[type=submit][name=tenantId]").Should().HaveCount(1);
        }
        var botones = cut.FindAll(".selector-tenant-panel form button[type=submit][name=tenantId]");
        botones.Select(b => b.GetAttribute("value")).Should().BeEquivalentTo(
            new[] { Norte, Sur, Origen }.Select(id => id.ToString()));
    }

    [Theory]
    [InlineData("trabajadores/11111111-2222-3333-4444-555555555555", "/trabajadores")]
    [InlineData("centros/11111111-2222-3333-4444-555555555555", "/centros")]
    [InlineData("empresas/11111111-2222-3333-4444-555555555555", "/empresas")]
    [InlineData("clientes/11111111-2222-3333-4444-555555555555?q=x", "/clientes")]
    [InlineData("proyectos/11111111-2222-3333-4444-555555555555?ctx=Proyecto:11111111-2222-3333-4444-555555555555:resumen", "/proyectos")]
    [InlineData("clientes/11111111-2222-3333-4444-555555555555/lectura-ia", "/clientes")]
    public async Task Desde_una_ficha_por_Id_la_ruta_de_retorno_es_la_lista_de_ese_arbol(string url, string lista)
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte, url: url);

        await Abrir(cut);

        foreach (var retorno in cut.FindAll("input[name=returnUrl]"))
            retorno.GetAttribute("value").Should().Be(lista,
                "la ficha pertenece al Tenant que se deja: en el nuevo se aterriza en la lista, no en «sin acceso» (contrato del selector § 4.4)");
    }

    [Theory]
    [InlineData("trabajadores")]
    [InlineData("trabajadores/no-es-un-guid")]
    [InlineData("configuracion/usuarios")]
    public async Task Fuera_de_una_ficha_por_Id_la_ruta_de_retorno_es_la_misma(string url)
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte, url: url);

        await Abrir(cut);

        cut.Find("input[name=returnUrl]").GetAttribute("value").Should().Be("/" + url);
    }

    [Fact]
    public async Task La_ruta_de_retorno_sigue_a_la_navegacion_del_circuito()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(url: "trabajadores");

        Services.GetRequiredService<NavigationManager>().NavigateTo("centros?q=x");
        await Abrir(cut);

        cut.Find("input[name=returnUrl]").GetAttribute("value").Should().Be("/centros",
            "el selector vive en el layout y sobrevive a las navegaciones: no vuelve a la ruta con la que arrancó");
    }

    [Fact]
    public async Task Escape_cierra_la_lista()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar();
        await Abrir(cut);
        cut.FindAll(".selector-tenant-panel").Should().ContainSingle();

        await cut.Find(".selector-tenant").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });

        cut.FindAll(".selector-tenant-panel").Should().BeEmpty();
    }

    [Fact]
    public async Task Navegar_cierra_la_lista_abierta()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar();
        await Abrir(cut);

        Services.GetRequiredService<NavigationManager>().NavigateTo("centros");

        cut.WaitForAssertion(() => cut.FindAll(".selector-tenant-panel").Should().BeEmpty());
    }

    // --- Búsqueda (más de 10 Empresas gestionadas, marco de Design del 20-10) ------------------------------------------------------------

    private static List<ClienteAutorizadoDto> Cartera(int n) =>
        Enumerable.Range(1, n)
            .Select(i => Gestionado(Guid.Parse($"cccccccc-0000-0000-0000-{i:D12}"), i == 3 ? "Nervión Instalaciones" : $"Empresa {i:D2}"))
            .ToList();

    [Fact]
    public async Task Con_diez_empresas_no_hay_buscador()
    {
        _lista = [Propio(), .. Cartera(10)];
        var cut = Renderizar();

        await Abrir(cut);

        cut.FindAll(".selector-tenant-busqueda").Should().BeEmpty();
        NombresDeLaLista(cut).Should().HaveCount(10);
    }

    [Fact]
    public async Task Con_once_empresas_hay_buscador_que_filtra_sin_distinguir_tildes_ni_mayusculas()
    {
        _lista = [Propio(), .. Cartera(11)];
        var cut = Renderizar();
        await Abrir(cut);

        var buscador = cut.Find(".selector-tenant-busqueda");
        await buscador.InputAsync(new ChangeEventArgs { Value = "NERVION" });

        NombresDeLaLista(cut).Should().Equal("Nervión Instalaciones");
        cut.FindAll("[role=option]").Last().TextContent.Should().Contain("Tu organización",
            "el origen queda fijo bajo la lista aunque el filtro no lo alcance");

        await cut.Find(".selector-tenant-busqueda").InputAsync(new ChangeEventArgs { Value = "zzz" });
        NombresDeLaLista(cut).Should().BeEmpty();
        cut.Find(".selector-tenant-vacio").TextContent.Should().Be("Ninguna empresa coincide con «zzz»");
    }

    [Fact]
    public async Task Con_58_empresas_gestionadas_el_titulo_las_cuenta_y_el_buscador_filtra_con_su_conteo()
    {
        _lista = [Propio(), .. Cartera(58)];
        var cut = Renderizar();
        await Abrir(cut);

        cut.Find(".selector-tenant-cabecera-titulo").TextContent.Trim().Should().Be("Empresas gestionadas · 58");
        cut.Find(".selector-tenant-busqueda").GetAttribute("aria-label").Should().Be("Buscar empresa gestionada");
        cut.Find(".selector-tenant-ayuda").TextContent.Should().Contain("↑↓").And.Contain("Enter",
            "sin texto, la línea de ayuda explica el teclado");
        cut.Find("#selector-tenant-resto").TextContent.Trim().Should().Be("Todas las empresas");
        NombresDeLaLista(cut).Should().HaveCount(58);

        await cut.Find(".selector-tenant-busqueda").InputAsync(new ChangeEventArgs { Value = "empresa 5" });

        // «Empresa 50»…«Empresa 58»: 9 de 58, en orden alfabético.
        var nombres = NombresDeLaLista(cut);
        nombres.Should().HaveCount(9);
        nombres.Should().BeInAscendingOrder(StringComparer.CurrentCulture);
        cut.Find(".selector-tenant-ayuda").TextContent.Should().Be("9 de 58 empresas");
        cut.Find("#selector-tenant-resto").TextContent.Trim().Should().Be("Resultados");
        cut.Find("[role=listbox][aria-labelledby=selector-tenant-resto]").Should().NotBeNull();
    }

    [Fact]
    public async Task Sin_resultados_el_estado_vacio_nombra_lo_buscado_y_no_ofrece_ninguna_empresa_gestionada()
    {
        _lista = [Propio(), .. Cartera(58)];
        var cut = Renderizar();
        await Abrir(cut);

        await cut.Find(".selector-tenant-busqueda").InputAsync(new ChangeEventArgs { Value = "  zzz  " });

        cut.Find(".selector-tenant-vacio").TextContent.Should().Be("Ninguna empresa coincide con «zzz»");
        cut.Find(".selector-tenant-ayuda").TextContent.Should().Be("0 de 58 empresas");
        cut.FindAll("#selector-tenant-resto").Should().BeEmpty("sin resultados no hay cabecera de lista");
        cut.FindAll("[role=option]").Should().ContainSingle("solo queda «Tu organización», que siempre está fija")
            .Which.TextContent.Should().Contain("Tu organización");
    }

    [Fact]
    public async Task El_teclado_del_buscador_se_apoya_en_el_listener_delegado_y_en_opciones_de_envio()
    {
        // Las flechas y Intro viven en microinteracciones.js, delegadas sobre [data-selector-tenant]: el
        // marcado ha de dejar al buscador dentro de ese contenedor, marcado, y a cada opción como botón de
        // envío de un <form> con el token antiforgery (elegir = el mismo POST de siempre, nunca un @onchange).
        _lista = [Propio(), .. Cartera(58)];
        var cut = Renderizar();
        await Abrir(cut);

        var buscador = cut.Find("[data-selector-tenant] input[type=search][data-selector-tenant-buscador]");
        buscador.Should().NotBeNull();
        cut.FindAll(".selector-tenant-panel form button[type=submit][role=option]").Should().HaveCount(59,
            "las 58 Empresas gestionadas y «Tu organización»");
        cut.Find(".selector-tenant-panel form").GetAttribute("action").Should().Be("/cuenta/cliente-activo");

        // Guardas de Intro: «Tu organización» queda fuera de lo que Intro puede elegir, y el JS espera a que el
        // servidor haya pintado el texto escrito (la carrera entre teclear e Intro).
        var origen = cut.Find("[data-selector-tenant-origen]");
        origen.QuerySelectorAll("button[type=submit][role=option]").Should().ContainSingle()
            .Which.TextContent.Should().Contain("Tu organización");
        buscador.GetAttribute("data-busqueda-pintada").Should().BeEmpty();

        await cut.Find(".selector-tenant-busqueda").InputAsync(new ChangeEventArgs { Value = "empresa 5" });

        cut.Find("[data-selector-tenant-buscador]").GetAttribute("data-busqueda-pintada").Should().Be("empresa 5");
    }

    [Fact]
    public async Task El_buscador_va_fuera_del_formulario_para_que_Intro_no_envie_un_POST_sin_Tenant()
    {
        _lista = [Propio(), .. Cartera(11)];
        var cut = Renderizar();
        await Abrir(cut);

        cut.FindAll(".selector-tenant-panel form .selector-tenant-busqueda").Should().BeEmpty();
        cut.FindAll(".selector-tenant-panel > .selector-tenant-busqueda").Should().ContainSingle();
    }

    [Fact]
    public async Task Los_nombres_largos_llevan_el_nombre_completo_en_title_y_en_aria_label()
    {
        const string largo = "Instalaciones Eléctricas y Mecánicas del Valle del Ebro y Levante Sociedad Limitada";
        _lista = [Propio(), Gestionado(Norte, largo), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte);

        await Abrir(cut);

        var fila = cut.FindAll("[role=option]").First(o => o.GetAttribute("value") == Norte.ToString());
        fila.GetAttribute("title").Should().Be(largo);
        fila.GetAttribute("aria-label").Should().Be(largo);
        cut.Find(".selector-tenant-disparador .selector-tenant-nombre").GetAttribute("title").Should().Be(largo);
    }

    // --- Avatar y ventana de revalidación ------------------------------------------------------------

    [Fact]
    public void Sin_logo_el_avatar_pinta_las_iniciales_y_con_logo_la_imagen()
    {
        var sinLogo = Render<AvatarTenant>(p => p.Add(a => a.Nombre, "Empresa Norte"));
        sinLogo.Find(".avatar-tenant").TextContent.Trim().Should().Be("EN");
        sinLogo.FindAll("img").Should().BeEmpty();
        sinLogo.Find(".avatar-tenant").GetAttribute("aria-hidden").Should().Be("true");

        var conLogo = Render<AvatarTenant>(p => p
            .Add(a => a.Nombre, "Empresa Norte")
            .Add(a => a.LogoUrl, "/tenants/abc/logo?v=2"));
        conLogo.Find("img").GetAttribute("src").Should().Be("/tenants/abc/logo?v=2");
        conLogo.Find(".avatar-tenant").TextContent.Trim().Should().BeEmpty();
    }

    [Fact]
    public async Task Con_version_de_logo_el_selector_pinta_la_imagen_del_endpoint_versionado()
    {
        _lista = [Propio(), new ClienteAutorizadoDto(Norte, "Empresa Norte", false, true, LogoVersion: "abcdef0123456789"),
            Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte);

        cut.Find(".selector-tenant-disparador img").GetAttribute("src").Should().Be($"/tenants/{Norte}/logo?v=abcdef0123456789");
        await Abrir(cut);
        cut.FindAll(".selector-tenant-fila img").Should().ContainSingle("solo el Tenant con logo lo pinta; el resto, iniciales");
        AvatarTenant.UrlDeLogo(Sur, null).Should().BeNull();
    }

    // --- Pendientes y «Recientes» (ficha 11) ----------------------------------------------------

    private static readonly Guid FueraDelAlcance = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000ff");

    private static MiTrabajoTenantDto ColaDe(Guid tenant, string nombre, int total, bool origen = false) => new(
        tenant, nombre, origen, new BandejaAgrupadaDto([], []), [], [],
        new ResumenMiTrabajoTenantDto(tenant, nombre, origen, total, 0, total, 0, 0), AlcanceCero: false);

    [Fact]
    public async Task Los_pendientes_por_empresa_gestionada_salen_del_calculo_de_Mi_trabajo_y_no_cuentan_ni_muestran_un_Tenant_fuera_del_alcance()
    {
        _lista = [Propio(gestionado: true), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _miTrabajo = () => Task.FromResult(new MiTrabajoAgregadoDto([
            ColaDe(Origen, "Operador de prueba", 7, origen: true), ColaDe(Norte, "Empresa Norte", 4),
            ColaDe(Sur, "Empresa Sur", 0), ColaDe(FueraDelAlcance, "Empresa ajena", 9)]));

        var cut = Renderizar(seleccionado: Norte);
        _cuantasVecesSeCalculoMiTrabajo.Should().Be(0, "control: antes de abrir la lista no se calcula nada");
        await Abrir(cut);
        cut.WaitForAssertion(() => _cuantasVecesSeCalculoMiTrabajo.Should().Be(1));

        // Solo Norte tiene pendientes: Sur trae 0, el origen no es empresa gestionada y la ajena no está autorizada.
        cut.WaitForAssertion(() => cut.FindAll(".selector-tenant-contador").Select(c => c.TextContent.Trim()).Should().Equal("4"));
        cut.Markup.Should().NotContain("Empresa ajena").And.NotContain(">9<").And.NotContain(">7<");
        cut.Find("[aria-label^='Empresa Norte']").GetAttribute("aria-label").Should().Be("Empresa Norte, 4 pendientes");
    }

    [Fact]
    public async Task Si_el_calculo_de_pendientes_falla_el_selector_se_abre_completo_y_sin_contador()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _miTrabajo = () => throw new InvalidOperationException("fallo simulado");

        var cut = Renderizar(seleccionado: Norte);
        _cuantasVecesSeCalculoMiTrabajo.Should().Be(0, "antes de abrir la lista no se calcula nada");
        await Abrir(cut);
        cut.WaitForAssertion(() => _cuantasVecesSeCalculoMiTrabajo.Should().Be(1));

        NombresDeLaLista(cut).Should().Equal("Empresa Norte", "Empresa Sur");
        cut.FindAll(".selector-tenant-contador").Should().BeEmpty();
    }

    [Fact]
    public async Task Un_calculo_de_pendientes_que_no_termina_no_bloquea_el_selector()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var nuncaTermina = new TaskCompletionSource<MiTrabajoAgregadoDto>();
        _miTrabajo = () => nuncaTermina.Task;

        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        NombresDeLaLista(cut).Should().Equal("Empresa Norte", "Empresa Sur");
        cut.FindAll(".selector-tenant-contador").Should().BeEmpty();
        nuncaTermina.SetResult(new MiTrabajoAgregadoDto([]));
    }

    [Fact]
    public void El_selector_no_calcula_pendientes_si_no_se_pinta()
    {
        _lista = [Propio()];

        Renderizar();

        _cuantasVecesSeCalculoMiTrabajo.Should().Be(0, "un usuario mono-Tenant no ve selector y no debe pagar el cálculo");
    }

    [Fact]
    public async Task Abrir_y_cerrar_la_lista_varias_veces_pide_los_pendientes_una_sola_vez_por_componente()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var peticiones = 0;
        Services.AddSingleton<IContadorPendientesSelectorTenant>(new ContadorContado(() => peticiones++));

        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);
        await Abrir(cut);
        await Abrir(cut);

        cut.WaitForAssertion(() => peticiones.Should().Be(1, "sin el guard cada apertura volvería a pedirlos"));
    }

    [Fact]
    public async Task Un_solo_pendiente_se_dice_en_singular_para_quien_usa_lector_de_pantalla()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _miTrabajo = () => Task.FromResult(new MiTrabajoAgregadoDto([ColaDe(Sur, "Empresa Sur", 1)]));

        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        cut.WaitForAssertion(() => cut.Find("[aria-label^='Empresa Sur']").GetAttribute("aria-label").Should().Be("Empresa Sur, 1 pendiente"));
    }

    private sealed class ContadorContado(Action alPedir) : IContadorPendientesSelectorTenant
    {
        public Task<IReadOnlyDictionary<Guid, int>?> ObtenerAsync(
            Guid usuarioId, Guid tenantActivoId, IReadOnlyList<ClienteAutorizadoDto> autorizados,
            Func<CancellationToken, Task<MiTrabajoAgregadoDto>> calcular, CancellationToken cancellationToken)
        {
            alPedir();
            return Task.FromResult<IReadOnlyDictionary<Guid, int>?>(null);
        }

        public void Invalidar(Guid usuarioId) { }
    }

    [Fact]
    public async Task Los_recientes_salen_primero_y_se_recortan_al_conjunto_autorizado()
    {
        _lista = [Propio(gestionado: true), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur"),
            Gestionado(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004"), "Empresa Este")];
        _recientes = [Sur, FueraDelAlcance, Origen, Norte];

        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        cut.Find("#selector-tenant-recientes").TextContent.Should().Be("Recientes");
        cut.Find("[aria-labelledby='selector-tenant-recientes']").QuerySelectorAll(".selector-tenant-nombre")
            .Select(n => n.TextContent.Trim()).Should().Equal(["Empresa Sur"],
                "el Tenant fuera del alcance, el de origen y el activo no son recientes");
        NombresDeLaLista(cut).Should().Contain("Empresa Este");
        cut.FindAll(".selector-tenant-fila").Count(f => f.GetAttribute("title") == "Empresa Sur")
            .Should().Be(1, "un reciente no se repite en la lista de la cartera");
    }

    [Fact]
    public async Task Sin_recientes_validos_no_hay_seccion_Recientes()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _recientes = [FueraDelAlcance];

        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        cut.FindAll("#selector-tenant-recientes").Should().BeEmpty();
        NombresDeLaLista(cut).Should().Equal("Empresa Norte", "Empresa Sur");
    }

    [Theory]
    [InlineData("Refrielectric Norte", "RN")]
    [InlineData("  ", "?")]
    [InlineData("& Co", "C")]
    [InlineData("Uno Dos Tres", "UD")]
    public void Las_iniciales_salen_de_las_dos_primeras_palabras_con_letra(string nombre, string esperadas) =>
        AvatarTenant.Iniciales(nombre).Should().Be(esperadas);

    [Fact]
    public void La_ventana_de_lectura_tras_caducar_una_cartera_queda_acotada_a_60_s_de_revalidacion_y_60_s_de_memoizacion()
    {
        // I3: el circuito revalida cada 60 s y la memoización del alcance caduca a los 60 s como
        // máximo. Subir cualquiera de las dos alarga la ventana en la que un Tenant caducado sigue
        // dando lecturas: este test obliga a decidirlo a propósito.
        RevalidacionCircuitoActivoHandler.IntervaloPorDefectoSegundos.Should().Be(60);
        CaducidadAlcanceOptions.CaducidadPorDefecto.Should().Be(TimeSpan.FromSeconds(60));
        CaducidadAlcanceOptions.MaximoSegundos.Should().Be(60);
    }

    // --- Empresas fijadas (Design, 2026-10-17) ----------------------------------------------------

    private static readonly Guid Este = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000004");
    private const string ModuloFijadas = "./js/selector-tenant-fijadas.js";

    private BunitJSModuleInterop ConFijadas(params Guid[] guardadas)
    {
        var modulo = JSInterop.SetupModule(ModuloFijadas);
        modulo.Setup<string[]>("leerFijadas", _ => true).SetResult(guardadas.Select(g => g.ToString()).ToArray());
        modulo.SetupVoid("guardarFijadas", _ => true).SetVoidResult();
        return modulo;
    }

    private static async Task<IRenderedComponent<SelectorClienteActivo>> AbrirConFijadas(IRenderedComponent<SelectorClienteActivo> cut)
    {
        await Abrir(cut);
        return cut;
    }

    [Fact]
    public async Task Las_fijadas_salen_arriba_con_su_atajo_y_el_resto_de_la_cartera_debajo()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur"), Gestionado(Este, "Empresa Este")];
        ConFijadas(Sur);
        var cut = Renderizar(seleccionado: Norte);

        await AbrirConFijadas(cut);

        cut.WaitForAssertion(() => cut.Find("#selector-tenant-fijadas").TextContent.Trim().Should().Be("Fijadas"));
        cut.FindAll("#selector-tenant-resto").Should().BeEmpty("con fijadas encima la lista principal va sin rótulo «Resto de empresas»");
        NombresDeLaLista(cut).Should().Equal("Empresa Sur", "Empresa Este", "Empresa Norte");
        var fijadas = cut.Find("[aria-labelledby=selector-tenant-fijadas]");
        fijadas.QuerySelector(".selector-tenant-atajo")!.TextContent.Trim().Should().Be("Alt 1");
        cut.FindAll(".selector-tenant-atajo").Should().ContainSingle("solo la fijada lleva atajo");
        cut.Find("[role=listbox][aria-label=\"Todas las empresas\"]").QuerySelectorAll("[role=option]").Should().HaveCount(2);
    }

    [Fact]
    public async Task Una_fijada_que_ya_no_esta_autorizada_ni_la_organizacion_propia_se_pintan_como_fijadas()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var caducada = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000ff");
        var modulo = ConFijadas(caducada, Origen);
        var cut = Renderizar(seleccionado: Norte);

        await Abrir(cut);

        cut.FindAll("#selector-tenant-fijadas").Should().BeEmpty(
            "lo guardado en el navegador es preferencia: solo cuenta lo que el servidor ya autorizó, y sin la organización propia");
        cut.Find(".selector-tenant").GetAttribute("data-fijadas").Should().BeNullOrEmpty("Alt+n no tiene a dónde ir");

        // Al fijar otra empresa se guardan las tres (la nueva y las dos ajenas, que no se borran), pero solo la nueva cuenta.
        await cut.Find("[role=option][value='" + Norte + "']").ParentElement!.QuerySelector(".selector-tenant-fijar")!.ClickAsync(new MouseEventArgs());
        cut.Find(".selector-tenant").GetAttribute("data-fijadas").Should().Be(Norte.ToString());
        ((string[])modulo.Invocations["guardarFijadas"].Single().Arguments[1]!).Should().Equal(Norte.ToString(), caducada.ToString(), Origen.ToString());
        cut.Markup.Should().NotContain(caducada.ToString());
        cut.Find(".selector-tenant").GetAttribute("data-autorizados").Should().NotContain(Origen.ToString())
            .And.Contain(Norte.ToString()).And.Contain(Sur.ToString());
    }

    [Fact]
    public async Task La_estrella_fija_la_empresa_la_sube_a_Fijadas_y_guarda_la_lista()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var modulo = ConFijadas();
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);
        cut.FindAll("#selector-tenant-fijadas").Should().BeEmpty("sin fijadas no hay sección Fijadas");
        cut.Find("#selector-tenant-resto").TextContent.Trim().Should().Be("Todas las empresas",
            "como en el marco de Design, sin fijadas la lista principal se titula «Recientes»");

        var estrellaDeSur = cut.Find("[role=option][value='" + Sur + "']").ParentElement!.QuerySelector(".selector-tenant-fijar")!;
        estrellaDeSur.GetAttribute("aria-label").Should().Be("Fijar arriba");
        estrellaDeSur.GetAttribute("title").Should().Be("Fijar arriba");
        estrellaDeSur.GetAttribute("aria-pressed").Should().Be("false");
        estrellaDeSur.GetAttribute("type").Should().Be("button", "la estrella no envía el formulario de cambio de empresa");
        await estrellaDeSur.ClickAsync(new MouseEventArgs());

        cut.Find("#selector-tenant-fijadas").TextContent.Trim().Should().Be("Fijadas");
        NombresDeLaLista(cut).First().Should().Be("Empresa Sur");
        var fijada = cut.FindAll(".selector-tenant-fijar").Single(e => e.GetAttribute("aria-pressed") == "true");
        fijada.GetAttribute("aria-label").Should().Be("Quitar de fijadas");
        var guardado = modulo.Invocations["guardarFijadas"].Single();
        guardado.Arguments[0].Should().Be(UsuarioFalso.Id.ToString("N"), "la clave es del usuario: dos cuentas del mismo navegador no comparten");
        ((string[])guardado.Arguments[1]!).Should().Equal(Sur.ToString());
    }

    [Fact]
    public async Task Quitar_la_estrella_devuelve_la_empresa_al_resto_de_la_cartera()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var modulo = ConFijadas(Sur);
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);
        cut.WaitForAssertion(() => cut.FindAll("#selector-tenant-fijadas").Should().ContainSingle());

        await cut.Find(".selector-tenant-fijar[aria-pressed=true]").ClickAsync(new MouseEventArgs());

        cut.FindAll("#selector-tenant-fijadas").Should().BeEmpty();
        ((string[])modulo.Invocations["guardarFijadas"].Single().Arguments[1]!).Should().BeEmpty();
    }

    [Fact]
    public async Task La_organizacion_propia_no_lleva_estrella_y_las_filas_no_se_pueden_fijar_dos_veces()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        ConFijadas(Norte);
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);
        cut.WaitForAssertion(() => cut.FindAll("#selector-tenant-fijadas").Should().ContainSingle());

        var filaOrigen = cut.Find("[role=option][value='" + Origen + "']").ParentElement!;
        filaOrigen.QuerySelectorAll(".selector-tenant-fijar").Should().BeEmpty();
        cut.FindAll("[role=option]").Select(o => o.GetAttribute("value")).Should().OnlyHaveUniqueItems(
            "una empresa fijada sale en Fijadas y no otra vez en el resto");
    }

    [Fact]
    public void El_atajo_Alt_numero_cambia_de_empresa_por_el_mismo_POST_con_antiforgery_del_selector()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var cut = Renderizar(seleccionado: Norte, url: "trabajadores?q=ana");

        var formulario = cut.Find("form[data-selector-atajo]");
        formulario.GetAttribute("method").Should().Be("post");
        formulario.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
        formulario.HasAttribute("hidden").Should().BeTrue("existe aunque la lista esté cerrada, para que Alt+n funcione sin abrirla");
        formulario.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value").Should().Be("token-de-prueba");
        formulario.QuerySelector("input[name=returnUrl]")!.GetAttribute("value").Should().Be("/trabajadores");
        formulario.QuerySelector("input[name=tenantId]")!.GetAttribute("value").Should().BeNullOrEmpty(
            "el Id lo escribe selector-tenant-fijadas.js solo al pulsar el atajo");
    }

    [Fact]
    public async Task Quien_participa_en_la_incorporacion_a_cartera_ve_Solicitar_y_va_a_su_flujo()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _participaEnIncorporacion = true;
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        var solicitar = cut.Find(".selector-tenant-cabecera a.selector-tenant-solicitar");
        solicitar.TextContent.Trim().Should().Be("+ Solicitar");
        solicitar.GetAttribute("href").Should().Be("/cartera/solicitudes");
        solicitar.GetAttribute("title").Should().Be("Solicitar acceso a otra empresa de la organización");
        cut.Find(".selector-tenant-cabecera-titulo").TextContent.Trim().Should().StartWith("Empresas gestionadas");
    }

    [Fact]
    public async Task Si_todas_las_empresas_ya_estan_en_Fijadas_la_lista_principal_no_pinta_cabecera_vacia_ni_Sin_resultados()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        ConFijadas(Sur);
        var cut = Renderizar(seleccionado: Norte);

        await Abrir(cut);

        cut.WaitForAssertion(() => cut.Find("#selector-tenant-fijadas").TextContent.Trim().Should().Be("Fijadas"));
        // Norte (la activa) queda en la lista principal; fijando Norte también, la lista principal se vacía.
        await cut.Find("[role=option][value='" + Norte + "']").ParentElement!.QuerySelector(".selector-tenant-fijar")!.ClickAsync(new MouseEventArgs());

        cut.FindAll("#selector-tenant-resto").Should().BeEmpty("sin filas no hay cabecera de lista");
        cut.FindAll(".selector-tenant-vacio").Should().BeEmpty("«Sin resultados» solo tiene sentido al buscar");
    }

    [Fact]
    public async Task Quien_no_participa_en_la_incorporacion_a_cartera_no_ve_Solicitar()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _participaEnIncorporacion = false;
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        cut.FindAll(".selector-tenant-solicitar").Should().BeEmpty("la página de destino le negaría el acceso");
    }

    [Fact]
    public void Al_cambiar_de_empresa_la_pagina_nueva_avisa_Trabajando_en_la_empresa_activa()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var modulo = ConFijadas();
        modulo.Setup<bool>("cambioDeContexto", _ => true).SetResult(true);
        var cut = Renderizar(seleccionado: Norte);
        var toasts = Services.GetRequiredService<CaeManager.Web.Components.DesignSystem.ToastService>();

        cut.WaitForAssertion(() =>
            toasts.Mensajes.Should().ContainSingle().Which.Mensaje.Should().Be("Trabajando en Empresa Norte"));
        ((string)modulo.Invocations["cambioDeContexto"].Single().Arguments[1]!).Should().Be(Norte.ToString());
    }

    [Fact]
    public void Sin_cambio_de_empresa_no_hay_aviso()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        var modulo = ConFijadas();
        modulo.Setup<bool>("cambioDeContexto", _ => true).SetResult(false);
        var cut = Renderizar(seleccionado: Norte);
        var toasts = Services.GetRequiredService<CaeManager.Web.Components.DesignSystem.ToastService>();

        cut.WaitForAssertion(() => modulo.Invocations["cambioDeContexto"].Should().ContainSingle());
        toasts.Mensajes.Should().BeEmpty("la primera carga de la pestaña o recargar la misma empresa no avisan");
    }

    [Fact]
    public void El_panel_mide_300_px_y_la_barra_lateral_lo_deja_desbordar()
    {
        var css = File.ReadAllText(Ruta("Components", "Layout", "SelectorClienteActivo.razor.css"));
        var layout = File.ReadAllText(Ruta("Components", "Layout", "MainLayout.razor.css"));

        css.Should().Contain("min(300px");
        layout.Should().Contain(".barra-lateral:has(.selector-tenant-panel)");
    }

    private static string Ruta(params string[] partes)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CaeManager.Web")))
            dir = dir.Parent;
        return Path.Combine([dir!.FullName, "src", "CaeManager.Web", .. partes]);
    }

    // --- Selector de papel (D-4): una entrada por Asignación de Operación -----------------------

    private static ClienteAutorizadoDto ConPapel(Guid id, string nombre, ServicioCae servicio) =>
        Gestionado(id, nombre) with { Servicio = servicio };

    /// <summary>El formulario de la entrada de ese Tenant con ese papel (o sin papel), dentro de un grupo dado.</summary>
    private static AngleSharp.Dom.IElement? FormularioDe(
        IRenderedComponent<SelectorClienteActivo> cut, Guid tenant, ServicioCae? servicio, string ambito = ".selector-tenant-panel") =>
        cut.FindAll($"{ambito} form.selector-tenant-fila-envoltorio").FirstOrDefault(f =>
            f.QuerySelector($"button[name='tenantId'][value='{tenant}']") is not null
            && f.QuerySelector("input[name='servicio']")?.GetAttribute("value") == servicio?.ToString());

    private void DosPapelesSobreNorte()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _listaPorPapel =
        [
            Propio(), ConPapel(Norte, "Empresa Norte", ServicioCae.Outbound), ConPapel(Norte, "Empresa Norte", ServicioCae.Inbound),
            ConPapel(Sur, "Empresa Sur", ServicioCae.Outbound),
        ];
    }

    [Fact]
    public async Task D4_cada_Asignacion_de_Operacion_es_una_entrada_con_su_pastilla_y_envia_su_servicio()
    {
        DosPapelesSobreNorte();
        var cut = Renderizar(seleccionado: Sur);
        await Abrir(cut);

        cut.FindAll(".selector-tenant-lista .selector-tenant-papel").Select(p => p.TextContent.Trim())
            .Should().BeEquivalentTo(["Acredita", "Valida", "Acredita"]);

        var inbound = FormularioDe(cut, Norte, ServicioCae.Inbound);
        inbound.Should().NotBeNull("la entrada Inbound de Empresa Norte envía servicio=Inbound");
        inbound!.GetAttribute("method").Should().Be("post");
        inbound.GetAttribute("action").Should().Be("/cuenta/cliente-activo");
        inbound.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value").Should().Be("token-de-prueba");
        inbound.QuerySelector(".selector-tenant-papel")!.TextContent.Trim().Should().Be("Valida");
        inbound.QuerySelector("button")!.GetAttribute("aria-label").Should().Be("Empresa Norte · Valida");
        FormularioDe(cut, Norte, ServicioCae.Outbound).Should().NotBeNull("la de Outbound es otra entrada del mismo Tenant");

        // El origen no tiene papel: su formulario no envía servicio.
        FormularioDe(cut, Origen, null, "[data-selector-tenant-origen]").Should().NotBeNull();
    }

    /// <summary>
    /// O-2 (b), Chris 2026-11-17: el selector de papel no mira el rol. El Administrador, la Dirección CAE, el
    /// Coordinador CAE y la Consulta del Operador CAE externo que llegan por una Asignación de Operación Inbound reciben
    /// sus entradas con pastilla (la consulta las decide con los predicados del POST) y el selector las pinta.
    /// </summary>
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("Consulta")]
    public async Task O2_otro_perfil_del_Operador_CAE_externo_con_Asignacion_de_Operacion_ve_el_selector_de_papel(string rol)
    {
        _rol = rol;
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte") with { EsCarteraGestorCae = false }];
        _listaPorPapel =
        [
            Propio(), ConPapel(Norte, "Empresa Norte", ServicioCae.Outbound) with { EsCarteraGestorCae = false },
            ConPapel(Sur, "Empresa Sur", ServicioCae.Inbound) with { EsCarteraGestorCae = false },
        ];

        var cut = Renderizar();
        cut.FindAll(".selector-tenant").Should().NotBeEmpty();
        await Abrir(cut);

        cut.FindAll(".selector-tenant-lista .selector-tenant-papel").Select(p => p.TextContent.Trim())
            .Should().BeEquivalentTo(["Acredita", "Valida"]);
        FormularioDe(cut, Sur, ServicioCae.Inbound).Should().NotBeNull("la entrada Inbound envía servicio=Inbound al POST, que revalida");
    }

    [Fact]
    public void O2_el_Administrador_del_Tenant_propio_sigue_sin_selector_de_papel()
    {
        // Sin ninguna Asignación de Operación la consulta devuelve la lista sin papel (lo fija el handler real en
        // ObtenerClientesAutorizadosPorPapelTests); el selector no pinta nada.
        _rol = "Administrador";
        _lista = [Propio()];
        _listaPorPapel = [Propio()];

        var cut = Renderizar();

        cut.FindAll(".selector-tenant").Should().BeEmpty();
        cut.FindAll(".selector-tenant-papel").Should().BeEmpty();
    }

    [Fact]
    public async Task D4_la_entrada_activa_es_la_del_Tenant_y_el_papel_que_resolvio_el_servidor()
    {
        DosPapelesSobreNorte();
        _papel = PapelActivo.Inbound;
        var cut = Renderizar(seleccionado: Norte);

        cut.Find(".selector-tenant-disparador .selector-tenant-papel").TextContent.Trim().Should().Be("Valida");
        await Abrir(cut);

        FormularioDe(cut, Norte, ServicioCae.Inbound)!.QuerySelector("button")!.GetAttribute("aria-selected").Should().Be("true");
        FormularioDe(cut, Norte, ServicioCae.Outbound)!.QuerySelector("button")!.GetAttribute("aria-selected").Should().Be("false",
            "un papel cada vez: la otra entrada del mismo Tenant no está activa");
    }

    [Fact]
    public async Task D4_las_recientes_preseleccionan_el_ultimo_papel_recordado_para_ese_Tenant()
    {
        DosPapelesSobreNorte();
        _recientes = [Norte];
        _papelesRecordados = new Dictionary<Guid, ServicioCae> { [Norte] = ServicioCae.Inbound };
        var cut = Renderizar(seleccionado: Sur);
        await Abrir(cut);

        FormularioDe(cut, Norte, ServicioCae.Inbound, "[aria-labelledby='selector-tenant-recientes']")
            .Should().NotBeNull("la reciente de Empresa Norte abre el papel que eligió la última vez");
        FormularioDe(cut, Norte, ServicioCae.Outbound, "[aria-labelledby='selector-tenant-recientes']").Should().BeNull();
        FormularioDe(cut, Norte, ServicioCae.Outbound, ".selector-tenant-lista").Should().NotBeNull(
            "la otra entrada del mismo Tenant sigue en la lista");
    }

    [Fact]
    public async Task D4_un_papel_recordado_que_ya_no_esta_entre_las_entradas_se_ignora()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _listaPorPapel = [Propio(), ConPapel(Norte, "Empresa Norte", ServicioCae.Outbound), ConPapel(Sur, "Empresa Sur", ServicioCae.Outbound)];
        _recientes = [Norte];
        _papelesRecordados = new Dictionary<Guid, ServicioCae> { [Norte] = ServicioCae.Inbound };
        var cut = Renderizar(seleccionado: Sur);
        await Abrir(cut);

        FormularioDe(cut, Norte, ServicioCae.Outbound, "[aria-labelledby='selector-tenant-recientes']")
            .Should().NotBeNull("sin entrada Inbound vigente, la reciente abre la que haya");
    }

    [Fact]
    public void D4_una_entrada_Inbound_hace_visible_el_selector_aunque_la_lista_por_Tenant_no_lo_pida()
    {
        _lista = [Propio()];
        _listaPorPapel = [Propio(), ConPapel(Norte, "Empresa Norte", ServicioCae.Inbound)];

        Renderizar().FindAll(".selector-tenant").Should().NotBeEmpty("el selector es el único sitio donde se elige el papel Inbound");
    }

    [Fact]
    public async Task D4_la_via_heredada_conserva_su_entrada_sin_papel_junto_a_la_de_Inbound_del_mismo_Tenant()
    {
        // Norte llega por la vía heredada (sin servicio) y además por una Asignación de Operación Inbound.
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _listaPorPapel =
        [
            Propio(), Gestionado(Norte, "Empresa Norte"), ConPapel(Norte, "Empresa Norte", ServicioCae.Inbound),
            ConPapel(Sur, "Empresa Sur", ServicioCae.Outbound),
        ];
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        FormularioDe(cut, Norte, null).Should().NotBeNull("la vía heredada sigue siendo una opción autorizada");
        FormularioDe(cut, Norte, ServicioCae.Inbound).Should().NotBeNull();
        FormularioDe(cut, Norte, null)!.QuerySelector("button")!.GetAttribute("aria-selected").Should().Be("true",
            "sin papel resuelto, la entrada activa es la de la vía heredada");
        FormularioDe(cut, Norte, ServicioCae.Inbound)!.QuerySelector("button")!.GetAttribute("aria-selected").Should().Be("false");
    }

    [Fact]
    public async Task D4_con_el_papel_Inbound_resuelto_la_entrada_sin_papel_del_mismo_Tenant_no_esta_activa()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte"), Gestionado(Sur, "Empresa Sur")];
        _listaPorPapel =
        [
            Propio(), Gestionado(Norte, "Empresa Norte"), ConPapel(Norte, "Empresa Norte", ServicioCae.Inbound),
            ConPapel(Sur, "Empresa Sur", ServicioCae.Outbound),
        ];
        _papel = PapelActivo.Inbound;
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        FormularioDe(cut, Norte, ServicioCae.Inbound)!.QuerySelector("button")!.GetAttribute("aria-selected").Should().Be("true");
        FormularioDe(cut, Norte, null)!.QuerySelector("button")!.GetAttribute("aria-selected").Should().Be("false",
            "un listbox de selección única: solo la entrada del papel resuelto está activa");
        cut.FindAll(".selector-tenant-lista [aria-selected='true']").Should().HaveCount(1);
    }

    [Fact]
    public void D4_el_disparador_anuncia_el_papel_activo_al_lector_de_pantalla()
    {
        DosPapelesSobreNorte();
        _papel = PapelActivo.Inbound;
        var disparador = Renderizar(seleccionado: Norte).Find(".selector-tenant-disparador");

        disparador.GetAttribute("aria-label").Should().EndWith(": Empresa Norte · Valida",
            "la pastilla es aria-hidden: el papel solo llega al lector de pantalla por la etiqueta");
    }

    /// <summary>
    /// Con papel Inbound, /trabajadores la cierra NucleoCerradoConPapelInbound: la entrada Inbound vuelve al inicio en vez
    /// de aterrizar en «Acceso denegado»; la de Outbound conserva la ruta. El formulario de Alt+1…5 lleva las dos.
    /// </summary>
    [Fact]
    public async Task D4_la_entrada_Inbound_desde_una_pagina_cerrada_con_Inbound_vuelve_al_inicio()
    {
        DosPapelesSobreNorte();
        var cut = Renderizar(seleccionado: Sur, url: "trabajadores");
        await Abrir(cut);

        FormularioDe(cut, Norte, ServicioCae.Inbound)!.QuerySelector("input[name='returnUrl']")!.GetAttribute("value").Should().Be("/");
        FormularioDe(cut, Norte, ServicioCae.Outbound)!.QuerySelector("input[name='returnUrl']")!.GetAttribute("value").Should().Be("/trabajadores");
        var atajo = cut.Find("form[data-selector-atajo]");
        atajo.GetAttribute("data-retorno").Should().Be("/trabajadores");
        atajo.GetAttribute("data-retorno-inbound").Should().Be("/");
    }

    [Fact]
    public async Task D4_la_entrada_Inbound_desde_una_ficha_de_Inbound_vuelve_a_su_lista()
    {
        DosPapelesSobreNorte();
        var cut = Renderizar(seleccionado: Sur, url: "inbound/contratos/" + Guid.NewGuid());
        await Abrir(cut);

        FormularioDe(cut, Norte, ServicioCae.Inbound)!.QuerySelector("input[name='returnUrl']")!.GetAttribute("value")
            .Should().Be("/inbound/contratos");
    }

    /// <summary>
    /// Caso puro D-4: solo un Tenant beneficiario alcanzado solo por Inbound. Lo hace visible su entrada Inbound, y con el
    /// mismo criterio se cargan las fijadas: la guardada se pinta y Alt+1…5 la tiene.
    /// </summary>
    [Fact]
    public void D4_con_solo_un_Tenant_alcanzado_por_Inbound_se_cargan_sus_fijadas()
    {
        _lista = [Propio()];
        _listaPorPapel = [Propio(), ConPapel(Norte, "Empresa Norte", ServicioCae.Inbound)];
        ConFijadas(Norte);

        var cut = Renderizar();

        cut.WaitForAssertion(() => cut.Find(".selector-tenant").GetAttribute("data-fijadas").Should().Be(Norte.ToString()));
        cut.Find(".selector-tenant").GetAttribute("data-fijadas-servicios").Should().Be($"{Norte}:Inbound");
    }

    /// <summary>
    /// Mixto: Norte por Outbound y Sur solo por Inbound. Sur se puede fijar con su estrella y una Sur ya guardada no se
    /// trata como ajena: lo que se puede fijar es lo que se pinta, no solo la lista por Tenant.
    /// </summary>
    [Fact]
    public async Task D4_un_Tenant_alcanzado_solo_por_Inbound_se_fija_y_no_se_trata_como_ajeno()
    {
        _lista = [Propio(), Gestionado(Norte, "Empresa Norte")];
        _listaPorPapel = [Propio(), ConPapel(Norte, "Empresa Norte", ServicioCae.Outbound), ConPapel(Sur, "Empresa Sur", ServicioCae.Inbound)];
        var modulo = ConFijadas(Sur);
        var cut = Renderizar(seleccionado: Norte);
        await Abrir(cut);

        cut.WaitForAssertion(() => cut.Find(".selector-tenant").GetAttribute("data-fijadas").Should().Be(Sur.ToString()));

        await cut.Find("[role=option][value='" + Sur + "']").ParentElement!.QuerySelector(".selector-tenant-fijar")!.ClickAsync(new MouseEventArgs());

        cut.Find(".selector-tenant").GetAttribute("data-fijadas").Should().BeNullOrEmpty("la estrella de Sur la desfija");
        ((string[])modulo.Invocations["guardarFijadas"].Single().Arguments[1]!).Should().BeEmpty(
            "Sur no estaba entre las ajenas: al desfijarla no queda guardada");
    }
}
