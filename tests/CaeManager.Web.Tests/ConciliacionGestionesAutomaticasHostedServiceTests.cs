using CaeManager.Application.Common;
using CaeManager.Application.Gestiones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Gestiones;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La orquestación de la conciliación de Gestiones automáticas: un Tenant propietario cada vez, un ámbito por lote, un
/// lote o un Tenant fallido que no corta el resto ni da la pasada por completa, y los reintentos hasta completar una (los
/// hallazgos abiertos del mismo patrón en la PR #2074, evitados aquí desde el principio). La parte de datos, bajo
/// <c>cae_app_runtime</c>, está en <c>ConciliacionGestionesAutomaticasBajoRuntimeTests</c>.
/// </summary>
public class ConciliacionGestionesAutomaticasHostedServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTime Desde = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Cada_busqueda_y_cada_lote_corren_en_el_ambito_de_su_Tenant_propietario()
    {
        var conciliacion = new ConciliacionFalsa();
        conciliacion.SinGestion[TenantA] = Ids(3);
        conciliacion.SinGestion[TenantB] = Ids(1);

        var resultado = await Disparador(conciliacion).ProcesarAsync(Desde, CancellationToken.None);

        conciliacion.Busquedas.Should().Equal((TenantA, Desde), (TenantB, Desde));
        conciliacion.Lotes.Select(l => (l.Tenant, l.Ids.Count)).Should().Equal((TenantA, 3), (TenantB, 1));
        conciliacion.Lotes.Should().OnlyContain(l => conciliacion.SinGestion[l.Tenant].Intersect(l.Ids).Count() == l.Ids.Count,
            "un lote solo lleva Documentos que encontró la búsqueda de su mismo Tenant");
        resultado.Completa.Should().BeTrue();
        resultado.Creadas.Should().Be(4);
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull("el ámbito de cada Tenant se cierra al terminar con él");
    }

    [Fact]
    public async Task Un_lote_fallido_no_corta_el_resto_del_Tenant_ni_los_demas_y_la_pasada_no_es_completa()
    {
        var conciliacion = new ConciliacionFalsa();
        conciliacion.SinGestion[TenantA] = Ids(ConciliacionGestionesAutomaticasService.DocumentosPorLote * 2 + 1);
        conciliacion.SinGestion[TenantB] = Ids(2);
        conciliacion.LoteQueFalla = 0;

        var resultado = await Disparador(conciliacion).ProcesarAsync(Desde, CancellationToken.None);

        conciliacion.Lotes.Select(l => l.Tenant).Should().Equal([TenantA, TenantA, TenantA, TenantB],
            "tras el lote fallido se intentan los dos que quedan del Tenant y el del otro Tenant");
        resultado.Lotes.Should().Be(4);
        resultado.LotesFallidos.Should().Be(1);
        resultado.Creadas.Should().Be(ConciliacionGestionesAutomaticasService.DocumentosPorLote + 1 + 2,
            "lo de los lotes fallidos no cuenta como creado");
        resultado.Completa.Should().BeFalse("un lote fallido deja la pasada incompleta");
    }

    [Fact]
    public async Task Un_Tenant_cuya_busqueda_falla_no_impide_conciliar_el_otro()
    {
        var conciliacion = new ConciliacionFalsa { TenantQueFallaAlBuscar = TenantA };
        conciliacion.SinGestion[TenantB] = Ids(2);

        var resultado = await Disparador(conciliacion).ProcesarAsync(Desde, CancellationToken.None);

        conciliacion.Lotes.Should().ContainSingle().Which.Tenant.Should().Be(TenantB);
        resultado.TenantsFallidos.Should().Be(1);
        resultado.Creadas.Should().Be(2);
        resultado.Completa.Should().BeFalse();
    }

    [Fact]
    public async Task La_cancelacion_se_propaga_y_no_se_cuenta_como_fallo()
    {
        using var parada = new CancellationTokenSource();
        var conciliacion = new ConciliacionFalsa { AlConciliar = () => parada.Cancel() };
        conciliacion.SinGestion[TenantA] = Ids(1);
        conciliacion.SinGestion[TenantB] = Ids(1);

        var pasada = () => Disparador(conciliacion).ProcesarAsync(Desde, parada.Token);

        await pasada.Should().ThrowAsync<OperationCanceledException>();
        conciliacion.Lotes.Should().ContainSingle("el Tenant siguiente no empieza tras la parada");
    }

    [Fact]
    public async Task Sin_liderazgo_reintenta_y_al_obtenerlo_hace_su_pasada_y_termina()
    {
        var conciliacion = new ConciliacionFalsa();
        conciliacion.SinGestion[TenantA] = Ids(1);
        var lider = new LiderPorTurnos(false, false, true);

        var completa = await Disparador(conciliacion, lider).EjecutarHastaCompletarAsync(CancellationToken.None);

        completa.Should().NotBeNull();
        lider.Intentos.Should().Be(3, "si el líder cae o no termina, esta instancia retoma en vez de rendirse");
        conciliacion.Busquedas.Should().ContainSingle();
    }

    [Fact]
    public async Task Una_pasada_incompleta_se_repite_hasta_completar_y_entonces_para()
    {
        var conciliacion = new ConciliacionFalsa { LoteQueFalla = 0 };
        conciliacion.SinGestion[TenantA] = Ids(1);
        var lider = new LiderPorTurnos(true, true, true, true);

        var completa = await Disparador(conciliacion, lider).EjecutarHastaCompletarAsync(CancellationToken.None);

        completa.Should().NotBeNull();
        completa!.Completa.Should().BeTrue();
        lider.Intentos.Should().Be(2, "la segunda pasada se completa y ya no hay más");
        conciliacion.Busquedas.Select(b => b.Desde).Should().Equal([Desde, Desde],
            "los reintentos miran la misma ventana, anclada al arranque");
    }

    [Fact]
    public async Task Agotados_los_intentos_sin_pasada_completa_termina_sin_resultado_y_sin_lanzar()
    {
        var conciliacion = new ConciliacionFalsa { TenantQueFallaAlBuscar = TenantA };
        var lider = new LiderPorTurnos(true, true, true);

        var completa = await Disparador(conciliacion, lider, maximoIntentos: 3).EjecutarHastaCompletarAsync(CancellationToken.None);

        completa.Should().BeNull();
        lider.Intentos.Should().Be(3);
    }

    [Fact]
    public async Task Un_fallo_al_pedir_el_liderazgo_se_reintenta()
    {
        var conciliacion = new ConciliacionFalsa();
        var lider = new LiderPorTurnos(true) { FallarEnElPrimero = true };

        var completa = await Disparador(conciliacion, lider).EjecutarHastaCompletarAsync(CancellationToken.None);

        completa.Should().NotBeNull();
        lider.Intentos.Should().Be(2);
    }

    // ---------- Montaje ----------

    private static IReadOnlyList<Guid> Ids(int cuantos) => Enumerable.Range(0, cuantos).Select(_ => Guid.NewGuid()).ToList();

    private static DisparadorConTenantsFijos Disparador(
        ConciliacionFalsa conciliacion, IEleccionLiderService? lider = null, int maximoIntentos = 12)
    {
        var servicios = new ServiceCollection()
            .AddScoped<IConciliacionGestionesAutomaticasService>(_ => conciliacion.NuevoAmbito())
            .BuildServiceProvider();
        var opciones = Options.Create(new ConciliacionGestionesAutomaticasOptions
        {
            EjecutarAlArrancar = true,
            // Ventana de un día hasta el arranque fijado por el reloj de abajo: Desde.
            Ventana = TimeSpan.FromDays(1),
            EsperaInicial = TimeSpan.Zero,
            IntervaloReintento = TimeSpan.Zero,
            MaximoIntentos = maximoIntentos,
        });
        return new DisparadorConTenantsFijos(
            servicios.GetRequiredService<IServiceScopeFactory>(), lider ?? new LiderPorTurnos(true), opciones,
            new RelojFijo(new DateTimeOffset(Desde.AddDays(1))));
    }

    private sealed class DisparadorConTenantsFijos(
        IServiceScopeFactory ambitos, IEleccionLiderService lider,
        IOptions<ConciliacionGestionesAutomaticasOptions> opciones, TimeProvider reloj)
        : ConciliacionGestionesAutomaticasHostedService(
            ambitos, lider, opciones, reloj, NullLogger<ConciliacionGestionesAutomaticasHostedService>.Instance)
    {
        protected internal override Task<IReadOnlyList<Guid>> ListarTenantsAsync(CancellationToken stoppingToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([TenantA, TenantB]);
    }

    /// <summary>Anota en qué Tenant se busca y se concilia cada lote; falla donde se le pide.</summary>
    private sealed class ConciliacionFalsa
    {
        public Dictionary<Guid, IReadOnlyList<Guid>> SinGestion { get; } = [];
        public List<(Guid Tenant, DateTime Desde)> Busquedas { get; } = [];
        public List<(Guid Tenant, IReadOnlyCollection<Guid> Ids)> Lotes { get; } = [];
        public Guid? TenantQueFallaAlBuscar { get; init; }
        public int? LoteQueFalla { get; set; }
        public Action? AlConciliar { get; init; }
        private int _ambitos;

        public IConciliacionGestionesAutomaticasService NuevoAmbito()
        {
            _ambitos++;
            return new Ambito(this);
        }

        private sealed class Ambito(ConciliacionFalsa mundo) : IConciliacionGestionesAutomaticasService
        {
            public Task<IReadOnlyList<Guid>> BuscarDocumentosSinGestionAsync(DateTime desdeUtc, CancellationToken cancellationToken = default)
            {
                var tenant = AmbitoTenantExplicito.TenantIdActual ?? throw new InvalidOperationException("Sin Tenant en curso.");
                mundo.Busquedas.Add((tenant, desdeUtc));
                if (tenant == mundo.TenantQueFallaAlBuscar)
                    throw new InvalidOperationException("Sin conexión.");
                return Task.FromResult(mundo.SinGestion.GetValueOrDefault(tenant, []));
            }

            public Task<int> ConciliarLoteAsync(IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken = default)
            {
                var tenant = AmbitoTenantExplicito.TenantIdActual ?? throw new InvalidOperationException("Sin Tenant en curso.");
                var numero = mundo.Lotes.Count;
                mundo.Lotes.Add((tenant, documentoIds));
                mundo.AlConciliar?.Invoke();
                if (numero == mundo.LoteQueFalla)
                {
                    // Falla una sola vez: el reintento de la pasada lo concilia.
                    mundo.LoteQueFalla = null;
                    throw new InvalidOperationException("Choque con el índice único.");
                }
                return Task.FromResult(documentoIds.Count);
            }
        }
    }

    /// <summary>Concede o niega el liderazgo según la lista, en orden; con él, ejecuta el trabajo como el real.</summary>
    private sealed class LiderPorTurnos(params bool[] turnos) : IEleccionLiderService
    {
        public int Intentos { get; private set; }
        public bool FallarEnElPrimero { get; init; }

        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            clave.Should().Be(ConciliacionGestionesAutomaticasHostedService.ClaveLiderazgo);
            var turno = Intentos++;
            if (FallarEnElPrimero && turno == 0)
                throw new InvalidOperationException("Sin conexión para el cerrojo.");
            var concedido = turnos[Math.Min(turno, turnos.Length - 1)];
            if (concedido)
                await trabajo(cancellationToken);
            return concedido;
        }
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
