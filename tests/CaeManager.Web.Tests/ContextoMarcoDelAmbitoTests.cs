using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Marco.Queries.ObtenerContextoMarco;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pieza 1.3 del plan de rendimiento: el contexto del marco se lee una vez por ámbito, nunca se reutiliza entre
/// usuarios, Tenants o selecciones distintas, ni con una Sesión Privilegiada, la lente de demo o la vista previa de
/// vocabulario, y viaja del prerenderizado al circuito solo si es del mismo contexto exacto y sigue vigente.
/// </summary>
public class ContextoMarcoDelAmbitoTests
{
    private static readonly Guid Origen = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Beneficiario = Guid.Parse("dddddddd-0000-0000-0000-000000000002");

    private sealed class Mediador : IMediator
    {
        public int Lecturas;
        public Func<int, Task<ContextoMarcoDto>> Responder { get; set; } = n => Task.FromResult(Marco($"lectura {n}"));

        public async Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is ObtenerContextoMarcoQuery
                ? (T)(object)await Responder(Interlocked.Increment(ref Lecturas))
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    private sealed class Usuario(Func<Guid?> id, Func<Guid?> origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(id());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("GestorCae");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(origen());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class Tenant(Func<Guid?> id, Func<Guid?> asignacion, Func<Guid?> sesion) : ITenantActual, IClienteActivoSeleccionado
    {
        public Guid? TenantId => id();
        public Guid? TenantIdSeleccionado => id() == Origen ? null : id();
        public Guid? AsignacionOperacionIdSeleccionada => asignacion();
        public Guid? SesionPrivilegiadaIdSeleccionada => sesion();
    }

    private sealed class SesionPrivilegiada(Func<bool> hay) : ICoordenadaSesionPrivilegiada
    {
        public bool HaySeleccionada => hay();
    }

    private sealed class VistaPrevia(Func<PerfilVocabularioTenant?> perfil) : IVistaVocabularioPreviewService
    {
        public PerfilVocabularioTenant? PerfilForzado => perfil();
    }

    private sealed class Lente(Func<VistaDemoEfectiva?> efectiva) : IVistaDemoActual
    {
        public Task<bool> EstaDisponibleAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<VistaDemoEfectiva?> ObtenerEfectivaAsync(CancellationToken cancellationToken = default) => Task.FromResult(efectiva());
        public Task<IReadOnlyList<GestorDeVistaDemo>> ObtenerGestoresElegiblesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<GestorDeVistaDemo>>([]);
        public Task<IReadOnlyList<Guid>?> ObtenerTenantIdsAcotadosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>?>(null);
    }

    private sealed class Reloj : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    private Guid? _usuario = Guid.Parse("dddddddd-0000-0000-0000-0000000000aa");
    private Guid? _origen = Origen;
    private Guid? _tenant = Origen;
    private bool _sesionPrivilegiada;
    private Guid? _asignacion;
    private Guid? _sesionPrivilegiadaSeleccionada;
    private PerfilVocabularioTenant? _vistaPrevia;
    private VistaDemoEfectiva? _lente;
    private readonly Mediador _mediador = new();
    private readonly Reloj _reloj = new();

    private static ContextoMarcoDto Marco(string nombre, int empresas = 1) => new(
        PerfilVocabularioTenant.Consultora, RotulosPrimeraPersona: false, EsAdministradorPlataforma: false,
        [.. Enumerable.Range(0, empresas).Select(i => new ClienteAutorizadoDto(Guid.NewGuid(), $"{nombre} {i}", EsOrigen: i == 0))],
        OrdenGrupos: null, OrdenEnlaces: null, ParticipaEnIncorporacionCartera: false, Avatar: null);

    private void Registrar(IServiceCollection servicios)
    {
        servicios.AddSingleton<IMediator>(_mediador);
        servicios.AddSingleton<TimeProvider>(_reloj);
        servicios.AddSingleton<ICurrentUserService>(new Usuario(() => _usuario, () => _origen));
        var tenant = new Tenant(() => _tenant, () => _asignacion, () => _sesionPrivilegiadaSeleccionada);
        servicios.AddSingleton<ITenantActual>(tenant);
        servicios.AddSingleton<IClienteActivoSeleccionado>(tenant);
        servicios.AddSingleton<ICoordenadaSesionPrivilegiada>(new SesionPrivilegiada(() => _sesionPrivilegiada));
        servicios.AddSingleton<IVistaVocabularioPreviewService>(new VistaPrevia(() => _vistaPrevia));
        servicios.AddSingleton<IVistaDemoActual>(new Lente(() => _lente));
        servicios.AddScoped<ContextoMarcoDelAmbito>();
    }

    private ContextoMarcoDelAmbito Ambito()
    {
        var servicios = new ServiceCollection();
        Registrar(servicios);
        return servicios.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ContextoMarcoDelAmbito>();
    }

    [Fact]
    public async Task Los_componentes_del_mismo_ambito_comparten_una_lectura_tambien_si_piden_a_la_vez()
    {
        var pendiente = new TaskCompletionSource<ContextoMarcoDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.Responder = _ => pendiente.Task;
        var ambito = Ambito();

        var peticiones = Enumerable.Range(0, 4).Select(_ => ambito.ObtenerAsync()).ToList();
        pendiente.SetResult(Marco("marco"));
        var resultados = await Task.WhenAll(peticiones);

        _mediador.Lecturas.Should().Be(1);
        resultados.Should().AllSatisfy(r => r.Should().BeSameAs(resultados[0]));
        (await ambito.ObtenerAsync()).Should().BeSameAs(resultados[0]);
        _mediador.Lecturas.Should().Be(1);
    }

    [Theory]
    [InlineData("otro-usuario")]
    [InlineData("otro-origen")]
    [InlineData("otro-tenant-efectivo")]
    [InlineData("otra-asignacion")]
    public async Task Un_cambio_de_contexto_vuelve_a_leer(string cambio)
    {
        var ambito = Ambito();
        var primera = await ambito.ObtenerAsync();

        switch (cambio)
        {
            case "otro-usuario": _usuario = Guid.NewGuid(); break;
            case "otro-origen": _origen = Guid.NewGuid(); break;
            case "otra-asignacion": _asignacion = Guid.NewGuid(); break;
            default: _tenant = Beneficiario; break;
        }
        var segunda = await ambito.ObtenerAsync();

        _mediador.Lecturas.Should().Be(2);
        segunda.Should().NotBeSameAs(primera);
    }

    [Fact]
    public async Task Caduca_con_la_memoizacion_de_las_Queries_que_compone()
    {
        var ambito = Ambito();
        await ambito.ObtenerAsync();

        _reloj.Ahora += MemoPorContexto.Caducidad - TimeSpan.FromMilliseconds(1);
        await ambito.ObtenerAsync();
        _mediador.Lecturas.Should().Be(1);

        _reloj.Ahora += TimeSpan.FromMilliseconds(1);
        await ambito.ObtenerAsync();
        _mediador.Lecturas.Should().Be(2);
    }

    [Fact]
    public async Task Un_Command_lo_descarta()
    {
        var ambito = Ambito();
        await ambito.ObtenerAsync();

        ((IMemoPorContexto)ambito).Invalidar();
        await ambito.ObtenerAsync();

        _mediador.Lecturas.Should().Be(2);
    }

    [Fact]
    public async Task Una_lectura_en_vuelo_cuando_llega_un_Command_se_entrega_pero_no_se_guarda()
    {
        var pendiente = new TaskCompletionSource<ContextoMarcoDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mediador.Responder = n => n == 1 ? pendiente.Task : Task.FromResult(Marco("tras el Command"));
        var ambito = Ambito();

        var enVuelo = ambito.ObtenerAsync();
        ambito.Invalidar();
        pendiente.SetResult(Marco("antes del Command"));

        (await enVuelo).ClientesAutorizados[0].Nombre.Should().Be("antes del Command 0");
        (await ambito.ObtenerAsync()).ClientesAutorizados[0].Nombre.Should().Be("tras el Command 0");
        _mediador.Lecturas.Should().Be(2);
    }

    [Fact]
    public async Task Una_lectura_fallida_no_se_queda()
    {
        _mediador.Responder = n => n == 1
            ? Task.FromException<ContextoMarcoDto>(new InvalidOperationException("sin conexión"))
            : Task.FromResult(Marco("segunda"));
        var ambito = Ambito();

        var primera = () => ambito.ObtenerAsync();
        await primera.Should().ThrowAsync<InvalidOperationException>();
        (await ambito.ObtenerAsync()).ClientesAutorizados[0].Nombre.Should().Be("segunda 0");
        _mediador.Lecturas.Should().Be(2);
    }

    [Theory]
    [InlineData("sesion-privilegiada")]
    [InlineData("sesion-privilegiada-en-la-seleccion")]
    [InlineData("vista-previa")]
    [InlineData("lente")]
    [InlineData("sin-usuario")]
    public async Task Sin_clave_segura_cada_peticion_lee(string escenario)
    {
        switch (escenario)
        {
            case "sesion-privilegiada": _sesionPrivilegiada = true; break;
            case "sesion-privilegiada-en-la-seleccion": _sesionPrivilegiadaSeleccionada = Guid.NewGuid(); break;
            case "vista-previa": _vistaPrevia = PerfilVocabularioTenant.ClienteDirecto; break;
            case "lente": _lente = new VistaDemoEfectiva(default, null); break;
            default: _usuario = null; break;
        }
        var ambito = Ambito();

        await ambito.ObtenerAsync();
        await ambito.ObtenerAsync();

        _mediador.Lecturas.Should().Be(2, "el Actor de Plataforma TALVEG, la lente y la vista previa no comparten lectura");
    }

    [Fact]
    public async Task Lo_leido_en_el_prerenderizado_lo_toma_el_circuito_sin_volver_a_leer()
    {
        var persistido = await PrerenderizarYPersistir(Marco("prerender"));
        _mediador.Lecturas.Should().Be(1);
        persistido.Should().NotBeNull();

        using var circuito = new BunitContext();
        var estadoCircuito = circuito.AddBunitPersistentComponentState();
        Registrar(circuito.Services);
        estadoCircuito.Persist(ContextoMarcoDelAmbito.ClavePersistencia, persistido!);
        var marco = await circuito.Services.GetRequiredService<ContextoMarcoDelAmbito>().ObtenerAsync();

        marco.ClientesAutorizados[0].Nombre.Should().Be("prerender 0");
        _mediador.Lecturas.Should().Be(1, "el circuito reutiliza lo que viajó en el estado persistido");
    }

    [Theory]
    [InlineData("otro-usuario")]
    [InlineData("otro-tenant-efectivo")]
    [InlineData("caducado")]
    public async Task El_circuito_descarta_lo_persistido_de_otro_contexto_o_caducado(string escenario)
    {
        var persistido = await PrerenderizarYPersistir(Marco("prerender"));

        switch (escenario)
        {
            case "otro-usuario": _usuario = Guid.NewGuid(); break;
            case "otro-tenant-efectivo": _tenant = Beneficiario; break;
            default: _reloj.Ahora += MemoPorContexto.Caducidad; break;
        }
        using var circuito = new BunitContext();
        var estadoCircuito = circuito.AddBunitPersistentComponentState();
        Registrar(circuito.Services);
        estadoCircuito.Persist(ContextoMarcoDelAmbito.ClavePersistencia, persistido!);
        var marco = await circuito.Services.GetRequiredService<ContextoMarcoDelAmbito>().ObtenerAsync();

        marco.ClientesAutorizados[0].Nombre.Should().Be("lectura 2 0");
        _mediador.Lecturas.Should().Be(2);
    }

    [Fact]
    public async Task Un_Command_en_el_circuito_antes_de_la_primera_lectura_descarta_lo_persistido()
    {
        var persistido = await PrerenderizarYPersistir(Marco("prerender"));

        using var circuito = new BunitContext();
        var estadoCircuito = circuito.AddBunitPersistentComponentState();
        Registrar(circuito.Services);
        estadoCircuito.Persist(ContextoMarcoDelAmbito.ClavePersistencia, persistido!);
        var ambito = circuito.Services.GetRequiredService<ContextoMarcoDelAmbito>();
        ((IMemoPorContexto)ambito).Invalidar();
        var marco = await ambito.ObtenerAsync();

        marco.ClientesAutorizados[0].Nombre.Should().Be("lectura 2 0", "lo persistido pudo leerse antes de la escritura");
    }

    [Fact]
    public async Task Lo_restaurado_no_se_vuelve_a_persistir()
    {
        var persistido = await PrerenderizarYPersistir(Marco("prerender"));

        using var circuito = new BunitContext();
        var estadoCircuito = circuito.AddBunitPersistentComponentState();
        Registrar(circuito.Services);
        estadoCircuito.Persist(ContextoMarcoDelAmbito.ClavePersistencia, persistido!);
        await circuito.Services.GetRequiredService<ContextoMarcoDelAmbito>().ObtenerAsync();
        estadoCircuito.TriggerOnPersisting();

        estadoCircuito.TryTake<ContextoMarcoDelAmbito.ContextoPersistido>(ContextoMarcoDelAmbito.ClavePersistencia, out _)
            .Should().BeFalse("lo que ya hizo su viaje no se reenvía");
        _mediador.Lecturas.Should().Be(1);
    }

    [Fact]
    public async Task Un_estado_demasiado_grande_no_viaja_al_circuito()
    {
        var enlaces = Enumerable.Range(0, 200).Select(i => $"/enlace-{i}-{new string('x', 50)}").ToList();
        var persistido = await PrerenderizarYPersistir(Marco("orden largo") with { OrdenEnlaces = enlaces });

        persistido.Should().BeNull("el mensaje de arranque del circuito tiene un tope");
    }

    [Fact]
    public async Task Una_cartera_grande_no_viaja_al_circuito()
    {
        var persistido = await PrerenderizarYPersistir(Marco("grande", ContextoMarcoDelAmbito.MaximoEmpresasPersistidas + 1));

        persistido.Should().BeNull("el estado vuelve al servidor en el mensaje de arranque del circuito");
    }

    [Fact]
    public async Task Con_Sesion_Privilegiada_nada_viaja_al_circuito()
    {
        _sesionPrivilegiada = true;

        var persistido = await PrerenderizarYPersistir(Marco("privilegiada"));

        persistido.Should().BeNull();
    }

    [Fact]
    public async Task Un_contexto_degradado_se_entrega_pero_no_se_guarda()
    {
        _mediador.Responder = n => Task.FromResult(n == 1 ? Marco("degradado") with { Degradado = true } : Marco("completo"));
        var ambito = Ambito();

        (await ambito.ObtenerAsync()).Degradado.Should().BeTrue("quien lo pidió recibe lo que se pudo leer");
        (await ambito.ObtenerAsync()).ClientesAutorizados[0].Nombre.Should().Be("completo 0");
        _mediador.Lecturas.Should().Be(2, "un contexto con una parte tolerada no se sirve 10 s");
    }

    [Fact]
    public async Task Un_contexto_degradado_no_viaja_al_circuito()
    {
        var persistido = await PrerenderizarYPersistir(Marco("degradado") with { Degradado = true });

        persistido.Should().BeNull();
    }

    [Fact]
    public async Task El_circuito_descarta_un_estado_persistido_degradado()
    {
        var persistido = await PrerenderizarYPersistir(Marco("prerender"));

        using var circuito = new BunitContext();
        var estadoCircuito = circuito.AddBunitPersistentComponentState();
        Registrar(circuito.Services);
        estadoCircuito.Persist(ContextoMarcoDelAmbito.ClavePersistencia,
            persistido! with { Marco = persistido.Marco with { Degradado = true } });
        var marco = await circuito.Services.GetRequiredService<ContextoMarcoDelAmbito>().ObtenerAsync();

        marco.ClientesAutorizados[0].Nombre.Should().Be("lectura 2 0");
        _mediador.Lecturas.Should().Be(2);
    }

    [Fact]
    public async Task Tras_lo_restaurado_el_circuito_solo_persiste_su_propia_lectura()
    {
        // Sustituye al antiguo flag «Restaurada», que no se podía distinguir: la suscripción al guardado solo nace con
        // una lectura propia, y esa lectura sustituye a la entrada restaurada.
        var persistido = await PrerenderizarYPersistir(Marco("prerender"));

        using var circuito = new BunitContext();
        var estadoCircuito = circuito.AddBunitPersistentComponentState();
        Registrar(circuito.Services);
        estadoCircuito.Persist(ContextoMarcoDelAmbito.ClavePersistencia, persistido!);
        var ambito = circuito.Services.GetRequiredService<ContextoMarcoDelAmbito>();
        (await ambito.ObtenerAsync()).ClientesAutorizados[0].Nombre.Should().Be("prerender 0");
        _tenant = Beneficiario;
        await ambito.ObtenerAsync();
        estadoCircuito.TriggerOnPersisting();

        estadoCircuito.TryTake<ContextoMarcoDelAmbito.ContextoPersistido>(ContextoMarcoDelAmbito.ClavePersistencia, out var nuevo)
            .Should().BeTrue();
        nuevo!.Marco.ClientesAutorizados[0].Nombre.Should().Be("lectura 2 0");
        nuevo.Contexto.Clave.TenantEfectivo.Should().Be(Beneficiario);
    }

    private sealed class Invalidador : IInvalidadorAlcance
    {
        public void Invalidar() { }
    }

    [Fact]
    public async Task Guardar_el_orden_del_menu_descarta_el_contexto_del_ambito()
    {
        // El Command que guarda el orden es un ICommand: InvalidacionAlcanceBehavior descarta el contexto antes y
        // después, y la lectura siguiente del mismo ámbito (el menú móvil del circuito) ya ve el orden nuevo.
        var ambito = Ambito();
        await ambito.ObtenerAsync();
        var comportamiento = new InvalidacionAlcanceBehavior<GuardarOrdenMenuLateralCommand, Result>(
            new Invalidador(), [ambito]);

        await comportamiento.Handle(
            new GuardarOrdenMenuLateralCommand(["plataforma"], ["/empresas"], Guid.NewGuid()),
            _ => Task.FromResult(Result.Exito()), CancellationToken.None);
        await ambito.ObtenerAsync();

        _mediador.Lecturas.Should().Be(2);
    }

    private sealed class Almacen(Func<IComponentRenderMode, bool> admite) : IPersistentComponentStateStore
    {
        public IReadOnlyDictionary<string, byte[]>? Guardado { get; private set; }
        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() =>
            Task.FromResult<IDictionary<string, byte[]>>(new Dictionary<string, byte[]>());
        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state)
        {
            Guardado = state;
            return Task.CompletedTask;
        }
        public bool SupportsRenderMode(IComponentRenderMode renderMode) => admite(renderMode);
    }

    // BL0006: Renderer y RenderBatch no son API estable fuera del framework. El gestor real de estado persistido solo
    // acepta un Renderer (para su Dispatcher), y es justo lo que este test necesita para no depender del doble de bUnit.
#pragma warning disable BL0006
    private sealed class RendererDePrueba() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw exception;
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solo_se_persiste_para_el_circuito_InteractiveServer(bool almacenDelCircuito)
    {
        // Con el gestor real del framework, no con el doble de bUnit (que ignora el modo): el registro lleva
        // RenderMode.InteractiveServer y el framework solo lo guarda en un almacén que admite ese modo.
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        await gestor.RestoreStateAsync(new Almacen(_ => true));
        var servicios = new ServiceCollection();
        Registrar(servicios);
        servicios.AddSingleton(gestor.State);
        var ambito = servicios.BuildServiceProvider().CreateScope().ServiceProvider.GetRequiredService<ContextoMarcoDelAmbito>();
        await ambito.ObtenerAsync();

        var almacen = new Almacen(modo => almacenDelCircuito
            ? modo is InteractiveServerRenderMode
            : modo is InteractiveWebAssemblyRenderMode);
        using var renderer = new RendererDePrueba();
        await gestor.PersistStateAsync(almacen, renderer);

        (almacen.Guardado?.ContainsKey(ContextoMarcoDelAmbito.ClavePersistencia) ?? false).Should().Be(almacenDelCircuito);
    }

    private async Task<ContextoMarcoDelAmbito.ContextoPersistido?> PrerenderizarYPersistir(ContextoMarcoDto marco)
    {
        _mediador.Responder = n => Task.FromResult(n == 1 ? marco : Marco($"lectura {n}"));
        using var prerender = new BunitContext();
        var estado = prerender.AddBunitPersistentComponentState();
        Registrar(prerender.Services);
        await prerender.Services.GetRequiredService<ContextoMarcoDelAmbito>().ObtenerAsync();
        estado.TriggerOnPersisting();
        return estado.TryTake<ContextoMarcoDelAmbito.ContextoPersistido>(ContextoMarcoDelAmbito.ClavePersistencia, out var persistido)
            ? persistido
            : null;
    }
}
