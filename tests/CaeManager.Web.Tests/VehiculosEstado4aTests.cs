using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Vehículos (/vehiculos) y el selector de empresa activa (lote 3,
/// CONTRATO-SELECTOR-TENANT-LOGOS § 4.2.3 estado 4a y § 4.4): quien gestiona empresas externas y
/// no tiene ninguna elegida ve «Selecciona una empresa de tu cartera», sin ninguna consulta de datos
/// del Tenant de origen. Mismo patrón y mismas garantías que Trabajadores y Clientes.
/// </summary>
public class VehiculosEstado4aTests : BunitContext
{
    private const string TextoEstado4a = "Selecciona una empresa de tu cartera";

    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaNorte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaSur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    /// <summary>La página importa ./js/atajos-lista.js; ese módulo queda fuera de lo que se observa aquí.</summary>
    public VehiculosEstado4aTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private SeleccionEmpresaGestionadaDePrueba Seleccion = new();

    private sealed class MediadorFalso : IMediator
    {
        public List<ClienteAutorizadoDto> Autorizados { get; } = [];
        public List<object> Enviadas { get; } = [];

        /// <summary>Si se fija, la resolución de la empresa activa espera a esta tarea.</summary>
        public Task? RetenerAutorizados { get; set; }

        /// <summary>El token con el que la página pidió los Tenants autorizados.</summary>
        public CancellationToken? TokenDeAutorizados { get; private set; }

        /// <summary>La resolución vuelve sin lanzar aunque se haya cancelado (un doble que ignora el token).</summary>
        public bool IgnorarCancelacionDeAutorizados { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerClientesAutorizadosQuery && RetenerAutorizados is { } espera)
            {
                TokenDeAutorizados = cancellationToken;
                await (IgnorarCancelacionDeAutorizados ? espera : espera.WaitAsync(cancellationToken));
            }

            return (TResponse)(request switch
            {
                ObtenerClientesAutorizadosQuery => (object)(IReadOnlyList<ClienteAutorizadoDto>)Autorizados.ToList(),
                ObtenerPerfilVocabularioActualQuery => PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasParaSelectorQuery => new[] { new EmpresaSelectorDto(Guid.NewGuid(), "Montajes Ebro S.L.") },
                ObtenerSubcontratasParaSelectorQuery => Array.Empty<SubcontrataSelectorDto>(),
                ObtenerVehiculosQuery q => new ResultadoPaginado<VehiculoListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
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

    private static MediadorFalso ConCartera(bool origenGestionado)
    {
        var mediador = new MediadorFalso();
        mediador.Autorizados.AddRange(
        [
            new ClienteAutorizadoDto(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: origenGestionado),
            new ClienteAutorizadoDto(EmpresaNorte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(EmpresaSur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
        ]);
        return mediador;
    }

    private void Registrar(MediadorFalso mediador, string url)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => Seleccion);
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());

        // Los filtros de URL son [SupplyParameterFromQuery]: se llega a ellos navegando.
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
    }

    private IRenderedComponent<Vehiculos> Renderizar(MediadorFalso mediador, string url = "vehiculos")
    {
        Registrar(mediador, url);
        var cut = Render<Vehiculos>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static int ConsultasDeDatos(MediadorFalso mediador) =>
        mediador.Enviadas.Count(e => e is ObtenerVehiculosQuery or ObtenerEmpresasParaSelectorQuery or ObtenerSubcontratasParaSelectorQuery);

    private static string Busqueda(Vehiculos pagina) =>
        (string)typeof(Vehiculos).GetField("_busqueda", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(pagina)!;

    /// <summary>
    /// Barrera positiva: las continuaciones de <c>OnInitializedAsync</c> y <c>OnParametersSet</c> se publican en
    /// el contexto del renderer, así que un negativo evaluado justo tras <c>SetResult</c> podría pasar antes de
    /// que corran. Se espera a que la resolución termine y se deja correr lo que la sigue.
    /// </summary>
    private static async Task EsperarFinDeResolucionAsync(Vehiculos pagina)
    {
        // Retirada del árbol, el componente ya no admite WaitForAssertion ni InvokeAsync: se sondea la instancia.
        var campo = typeof(Vehiculos).GetField("_resolviendoEmpresa", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var limite = DateTime.UtcNow.AddSeconds(5);
        while ((bool)campo.GetValue(pagina)! && DateTime.UtcNow < limite)
            await Task.Delay(10);

        ((bool)campo.GetValue(pagina)!).Should().BeFalse("la resolución en vuelo ya terminó");
        await Task.Delay(50);
    }

    [Fact]
    public void Sin_empresa_elegida_no_se_piden_los_datos_del_origen_ni_se_ofrece_crear()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador);

        cut.Markup.Should().Contain(TextoEstado4a, "control positivo: es el estado 4a");
        ConsultasDeDatos(mediador).Should().Be(0, "ni la lista, ni los catálogos de empresas y subcontratas del origen");
        cut.Markup.Should().NotContain("+ Nuevo vehículo");
        cut.FindAll(".barra-filtros").Should().BeEmpty();
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty("el origen no es la empresa elegida");
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista()
    {
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "vehiculos");

        var cut = Render<Vehiculos>();

        cut.FindAll(".barra-filtros").Should().BeEmpty();
        cut.Markup.Should().NotContain("+ Nuevo vehículo");
        ConsultasDeDatos(mediador).Should().Be(0);

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(TextoEstado4a));
        ConsultasDeDatos(mediador).Should().Be(0, "ni antes ni después de resolverse se pide la lista del origen");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var mediador = ConCartera(origenGestionado: true);

        var cut = Renderizar(mediador);

        cut.Markup.Should().NotContain(TextoEstado4a);
        cut.Find(".cabecera-empresa-activa").TextContent.Should().Contain("Operador de prueba");
        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Should().NotBeEmpty();
    }

    [Fact]
    public void Con_empresa_elegida_la_cabecera_la_nombra_y_la_lista_se_monta()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador);

        cut.Find(".cabecera-empresa-activa").TextContent.Should().Contain("Empresa Sur");
        cut.Markup.Should().NotContain(TextoEstado4a);
        cut.Markup.Should().Contain("+ Nuevo vehículo");
        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Should().NotBeEmpty();
    }

    [Fact]
    public void Un_usuario_mono_Tenant_ve_la_lista_sin_cabecera_ni_estado_4a()
    {
        var mediador = new MediadorFalso();
        mediador.Autorizados.Add(new ClienteAutorizadoDto(Origen, "Propia", EsOrigen: true));

        var cut = Renderizar(mediador);

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().NotContain(TextoEstado4a);
        mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Should().NotBeEmpty();
    }

    /// <summary>Un enlace con filtros no salta el estado 4a ni aplica el filtro de la URL a la lista del origen.</summary>
    [Fact]
    public void Sin_empresa_elegida_un_enlace_con_filtros_no_salta_el_estado_4a()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador, "vehiculos?q=Furgoneta&estado=Vencido");

        cut.Markup.Should().Contain(TextoEstado4a, "control positivo: es el estado 4a");
        ConsultasDeDatos(mediador).Should().Be(0);
        Busqueda(cut.Instance).Should().BeEmpty("el filtro de la URL no se aplica sin empresa elegida");
    }

    /// <summary>Control positivo del anterior: con empresa elegida, el mismo enlace sí fija la búsqueda.</summary>
    [Fact]
    public void Con_empresa_elegida_un_enlace_con_filtros_fija_la_busqueda()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);

        var cut = Renderizar(ConCartera(origenGestionado: false), "vehiculos?q=Furgoneta");

        Busqueda(cut.Instance).Should().Be("Furgoneta");
    }

    /// <summary>Salir de la página con la resolución en vuelo la cancela: no se repinta ni se piden datos de nadie.</summary>
    [Fact]
    public async Task Salir_de_la_pagina_con_la_empresa_activa_en_vuelo_cancela_la_resolucion()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "vehiculos");

        var cut = Render<Vehiculos>();
        mediador.TokenDeAutorizados.Should().NotBeNull("la página pidió la lista de Tenants autorizados");
        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeFalse("control positivo: sigue montada");

        var pagina = cut.Instance;
        await DisposeComponentsAsync();

        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeTrue();
        puerta.SetResult();
        await EsperarFinDeResolucionAsync(pagina);
        ConsultasDeDatos(mediador).Should().Be(0);
    }

    /// <summary>
    /// Si la resolución vuelve sin lanzar tras retirarse la página (contexto con empresa activa), la página
    /// tampoco sigue: ni lista ni catálogos de una página que ya no existe.
    /// </summary>
    [Fact]
    public async Task Una_resolucion_que_vuelve_sin_lanzar_tras_retirar_la_pagina_no_pide_datos()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        mediador.IgnorarCancelacionDeAutorizados = true;
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "vehiculos");

        var cut = Render<Vehiculos>();
        var pagina = cut.Instance;
        await DisposeComponentsAsync();
        puerta.SetResult();
        await EsperarFinDeResolucionAsync(pagina);

        ConsultasDeDatos(mediador).Should().Be(0);
        mediador.Enviadas.Should().OnlyContain(e => e is ObtenerClientesAutorizadosQuery,
            "tras retirarse solo consta la resolución que ya iba en vuelo");
    }

    /// <summary>
    /// Retirada la página con la resolución en vuelo, ComponentBase todavía invoca <c>OnParametersSet</c> con
    /// <c>_resolviendoEmpresa</c> ya en false: no se procesan los parámetros de la URL de un componente muerto.
    /// </summary>
    [Fact]
    public async Task Retirada_la_pagina_con_la_resolucion_en_vuelo_no_se_procesan_los_parametros_de_la_url()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;
        Registrar(mediador, "vehiculos?q=Furgoneta");

        var cut = Render<Vehiculos>();
        var pagina = cut.Instance;
        await DisposeComponentsAsync();
        puerta.SetResult();
        await EsperarFinDeResolucionAsync(pagina);

        // El componente está retirado y no hay DOM que mirar: se lee el estado que la URL habría fijado.
        Busqueda(pagina).Should().BeEmpty("una página retirada no atiende los parámetros de la URL");
    }

    /// <summary>
    /// S12 (lote 2a): el alta usa el kit DrawerFormulario, que pregunta al «Cancelar» como la X (D-05) y al salir de la página.
    /// Esta prueba fija que la pantalla le pasa su «hay cambios» y su estado: sin cambios cierra, con el nombre escrito pregunta.
    /// </summary>
    [Fact]
    public async Task El_alta_pregunta_al_cancelar_con_datos_escritos_y_sin_cambios_cierra()
    {
        var cut = Renderizar(ConCartera(origenGestionado: true));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("+ Nuevo vehículo"));

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo vehículo").ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.ComprobarQueCancelarSinCambiosCierraAsync(".drawer-pie", ".drawer-panel");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nuevo vehículo").ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.Find(".drawer-cuerpo input.campo-input").InputAsync(new ChangeEventArgs { Value = "Furgoneta de obra" });
        await cut.PulsarCancelarDelPieAsync(".drawer-pie");

        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }
}
