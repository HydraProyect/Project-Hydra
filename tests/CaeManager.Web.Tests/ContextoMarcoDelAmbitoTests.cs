using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Marco.Queries.ObtenerContextoMarco;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.VistaDemo;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
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
