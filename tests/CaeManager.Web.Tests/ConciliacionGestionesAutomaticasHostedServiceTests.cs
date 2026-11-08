using CaeManager.Application.Common;
using CaeManager.Application.Gestiones;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Gestiones;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La orquestación de la conciliación de Gestiones automáticas: un Tenant propietario cada vez, un ámbito por lote, un
/// lote fallido que se divide para que un Documento que falla siempre no arrastre a sus acompañantes, un Tenant fallido
/// que no corta el resto, y pasadas repetidas con una ventana fija en vez de una espera desde el arranque (hallazgos 3 y
/// 4 de la ronda 4 de Codex sobre la PR #2072). La parte de datos, bajo <c>cae_app_runtime</c>, está en
/// <c>ConciliacionGestionesAutomaticasBajoRuntimeTests</c>.
/// </summary>
public class ConciliacionGestionesAutomaticasHostedServiceTests
{
    private static readonly Guid TenantA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid TenantB = Guid.Parse("00000000-0000-0000-0000-00000000000b");
    private static readonly DateTime Desde = new(2026, 11, 9, 0, 0, 0, DateTimeKind.Utc);
    private const string DesdeTexto = "2026-11-09T00:00:00Z";

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
        conciliacion.Ambitos.Should().Be(4, "un ámbito de servicios para la búsqueda de cada Tenant y otro para cada lote");
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull("el ámbito de cada Tenant se cierra al terminar con él");
    }

    [Fact]
    public async Task Un_Documento_que_falla_siempre_no_impide_guardar_a_sus_acompanantes_y_la_pasada_no_es_completa()
    {
        var conciliacion = new ConciliacionFalsa();
        // Tres lotes en el Tenant A (100, 100 y 2), el primero con el Documento que falla, y uno en el B.
        conciliacion.SinGestion[TenantA] = Ids(ConciliacionGestionesAutomaticasService.DocumentosPorLote * 2 + 2);
        conciliacion.SinGestion[TenantB] = Ids(2);
        var venenoso = conciliacion.SinGestion[TenantA][7];
        conciliacion.DocumentoQueFallaSiempre = venenoso;

        var resultado = await Disparador(conciliacion).ProcesarAsync(Desde, CancellationToken.None);

        resultado.Lotes.Should().Be(4);
        resultado.LotesDivididos.Should().Be(1);
        resultado.DocumentosFallidos.Should().Be(1, "solo el Documento que falla");
        resultado.Creadas.Should().Be(ConciliacionGestionesAutomaticasService.DocumentosPorLote * 2 + 2 - 1 + 2,
            "sus 99 acompañantes se guardan uno a uno, y el resto de lotes y Tenants sigue");
        resultado.Completa.Should().BeFalse("un Documento fallido deja la pasada incompleta");
        conciliacion.Lotes.Where(l => l.Ids.Count == 1).Should().HaveCount(ConciliacionGestionesAutomaticasService.DocumentosPorLote,
            "el lote fallido se reintenta Documento a Documento, cada uno en su ámbito");
        conciliacion.Lotes.Where(l => l.Ids.Count == 1).Select(l => l.Tenant).Should().OnlyContain(t => t == TenantA);
    }

    [Fact]
    public async Task Un_lote_que_falla_una_vez_se_recupera_al_dividirlo_y_la_pasada_es_completa()
    {
        var conciliacion = new ConciliacionFalsa { LoteQueFalla = 0 };
        conciliacion.SinGestion[TenantA] = Ids(3);

        var resultado = await Disparador(conciliacion).ProcesarAsync(Desde, CancellationToken.None);

        resultado.LotesDivididos.Should().Be(1);
        resultado.DocumentosFallidos.Should().Be(0);
        resultado.Creadas.Should().Be(3);
        resultado.Completa.Should().BeTrue();
    }

    [Fact]
    public async Task Un_lote_de_un_solo_Documento_que_falla_no_se_reintenta_en_la_misma_pasada()
    {
        var conciliacion = new ConciliacionFalsa();
        conciliacion.SinGestion[TenantA] = Ids(1);
        conciliacion.DocumentoQueFallaSiempre = conciliacion.SinGestion[TenantA][0];

        var resultado = await Disparador(conciliacion).ProcesarAsync(Desde, CancellationToken.None);

        conciliacion.Lotes.Should().ContainSingle("dividirlo sería repetir el mismo lote");
        resultado.LotesDivididos.Should().Be(0);
        resultado.DocumentosFallidos.Should().Be(1);
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task La_cancelacion_dentro_del_ultimo_lote_se_propaga_y_no_se_divide_ni_se_cuenta_como_fallo(int documentos)
    {
        // En el último lote del último Tenant: si se contara como fallo, la pasada terminaría sin lanzar.
        // Con un solo Documento no hay división que vuelva a comprobar la parada: es el caso que lo fija.
        using var parada = new CancellationTokenSource();
        var conciliacion = new ConciliacionFalsa
        {
            AlConciliar = () =>
            {
                parada.Cancel();
                parada.Token.ThrowIfCancellationRequested();
            },
        };
        conciliacion.SinGestion[TenantB] = Ids(documentos);

        var pasada = () => Disparador(conciliacion).ProcesarAsync(Desde, parada.Token);

        await pasada.Should().ThrowAsync<OperationCanceledException>();
        conciliacion.Lotes.Should().ContainSingle().Which.Tenant.Should().Be(TenantB);
    }

    [Fact]
    public async Task La_cancelacion_dentro_de_la_ultima_busqueda_se_propaga_y_no_se_cuenta_como_Tenant_fallido()
    {
        // En la búsqueda del último Tenant: si se contara como Tenant fallido, la pasada terminaría sin lanzar.
        using var parada = new CancellationTokenSource();
        var busquedas = 0;
        var conciliacion = new ConciliacionFalsa
        {
            AlBuscar = () =>
            {
                if (++busquedas < 2)
                    return;
                parada.Cancel();
                parada.Token.ThrowIfCancellationRequested();
            },
        };

        var pasada = () => Disparador(conciliacion).ProcesarAsync(Desde, parada.Token);

        await pasada.Should().ThrowAsync<OperationCanceledException>();
        conciliacion.Busquedas.Select(b => b.Tenant).Should().Equal(TenantA, TenantB);
    }

    [Fact]
    public async Task Repite_la_pasada_aunque_salga_completa_hasta_agotar_la_duracion_y_siempre_con_la_ventana_fija()
    {
        // Hallazgo 3 de la ronda 4: lo que la ranura anterior confirme después de una pasada completa (el drenaje sigue)
        // lo recoge la pasada siguiente. El reloj avanza un minuto por consulta; la duración da para tres pasadas.
        var conciliacion = new ConciliacionFalsa();
        conciliacion.SinGestion[TenantA] = Ids(1);
        var reloj = new RelojQueAvanza(new DateTimeOffset(Desde.AddDays(5)), TimeSpan.FromMinutes(1));

        var pasadas = await Disparador(conciliacion, reloj: reloj, duracion: TimeSpan.FromMinutes(3)).EjecutarPasadasAsync(CancellationToken.None);

        pasadas.Should().HaveCount(3).And.OnlyContain(p => p.Completa);
        conciliacion.Busquedas.Should().HaveCount(6).And.OnlyContain(b => b.Desde == Desde,
            "la ventana sale de la opción, no del arranque: cinco días después sigue mirando el mismo relevo");
    }

    [Fact]
    public async Task Sin_liderazgo_espera_a_la_pasada_siguiente()
    {
        var conciliacion = new ConciliacionFalsa();
        conciliacion.SinGestion[TenantA] = Ids(1);
        var lider = new LiderPorTurnos(false, true, false);
        var reloj = new RelojQueAvanza(new DateTimeOffset(Desde), TimeSpan.FromMinutes(1));

        var pasadas = await Disparador(conciliacion, lider, reloj, TimeSpan.FromMinutes(3)).EjecutarPasadasAsync(CancellationToken.None);

        lider.Intentos.Should().Be(3);
        pasadas.Should().ContainSingle("solo la pasada con el liderazgo concilia");
        conciliacion.Busquedas.Should().Equal([(TenantA, Desde), (TenantB, Desde)]);
    }

    [Fact]
    public async Task Un_fallo_al_pedir_el_liderazgo_no_para_las_pasadas_siguientes()
    {
        var conciliacion = new ConciliacionFalsa();
        var lider = new LiderPorTurnos(true) { FallarEnElPrimero = true };
        var reloj = new RelojQueAvanza(new DateTimeOffset(Desde), TimeSpan.FromMinutes(1));

        var pasadas = await Disparador(conciliacion, lider, reloj, TimeSpan.FromMinutes(2)).EjecutarPasadasAsync(CancellationToken.None);

        lider.Intentos.Should().Be(2);
        pasadas.Should().ContainSingle();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11-09T00:00:00")]
    [InlineData("2026-11-09T01:00:00+01:00")]
    [InlineData("no es una fecha")]
    public async Task Sin_un_inicio_en_UTC_no_corre_y_no_tumba_el_host(string? desdeUtc)
    {
        var conciliacion = new ConciliacionFalsa();

        var pasadas = await Disparador(conciliacion, desdeUtc: desdeUtc).EjecutarPasadasAsync(CancellationToken.None);

        pasadas.Should().BeEmpty();
        conciliacion.Busquedas.Should().BeEmpty("sin ventana explícita no se adivina una");
    }

    [Fact]
    public void El_inicio_se_lee_en_UTC_exacto()
    {
        new ConciliacionGestionesAutomaticasOptions { DesdeUtc = DesdeTexto }.LeerDesdeUtc()
            .Should().Be(Desde).And.Subject!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public async Task Un_intervalo_no_positivo_no_corre()
    {
        var conciliacion = new ConciliacionFalsa();

        var pasadas = await Disparador(conciliacion, intervalo: TimeSpan.Zero).EjecutarPasadasAsync(CancellationToken.None);

        pasadas.Should().BeEmpty();
        conciliacion.Busquedas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_duracion_que_desborda_la_fecha_se_registra_y_no_tumba_el_host()
    {
        var conciliacion = new ConciliacionFalsa();

        var pasadas = await Disparador(conciliacion, duracion: TimeSpan.MaxValue).EjecutarPasadasAsync(CancellationToken.None);

        pasadas.Should().BeEmpty();
        conciliacion.Busquedas.Should().BeEmpty();
    }

    [Fact]
    public void Va_apagada_por_defecto_y_solo_se_registra_si_la_configuracion_la_enciende()
    {
        static bool Registrada(Dictionary<string, string?> valores)
        {
            var servicios = new ServiceCollection();
            var configuracion = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
            global::CaeManager.Infrastructure.DependencyInjection.InfrastructureServiceCollectionExtensions.AddConciliacionGestionesAutomaticas(servicios, configuracion);
            return servicios.Any(s => s.ImplementationType == typeof(ConciliacionGestionesAutomaticasHostedService));
        }

        var seccion = ConciliacionGestionesAutomaticasOptions.SeccionConfiguracion;
        Registrada([]).Should().BeFalse("sin configuración no corre: encenderla es decisión de quien despliega");
        Registrada(new() { [$"{seccion}:EjecutarAlArrancar"] = "false" }).Should().BeFalse();
        Registrada(new() { [$"{seccion}:EjecutarAlArrancar"] = "true" }).Should().BeTrue();
        new ConciliacionGestionesAutomaticasOptions().EjecutarAlArrancar.Should().BeFalse();
        new ConciliacionGestionesAutomaticasOptions().DesdeUtc.Should().BeNull("el inicio lo fija quien despliega");
    }

    // ---------- Montaje ----------

    private static IReadOnlyList<Guid> Ids(int cuantos) => Enumerable.Range(0, cuantos).Select(_ => Guid.NewGuid()).ToList();

    private static DisparadorConTenantsFijos Disparador(
        ConciliacionFalsa conciliacion, IEleccionLiderService? lider = null, TimeProvider? reloj = null,
        TimeSpan? duracion = null, string? desdeUtc = DesdeTexto, TimeSpan? intervalo = null)
    {
        var servicios = new ServiceCollection()
            .AddScoped<IConciliacionGestionesAutomaticasService>(_ => conciliacion.NuevoAmbito())
            .BuildServiceProvider();
        var opciones = Options.Create(new ConciliacionGestionesAutomaticasOptions
        {
            EjecutarAlArrancar = true,
            DesdeUtc = desdeUtc,
            IntervaloPasadas = intervalo ?? TimeSpan.FromMilliseconds(1),
            // Por defecto una sola pasada: el reloj fijo ya está en el final.
            DuracionPasadas = duracion ?? TimeSpan.Zero,
        });
        return new DisparadorConTenantsFijos(
            servicios.GetRequiredService<IServiceScopeFactory>(), lider ?? new LiderPorTurnos(true), opciones,
            reloj ?? new RelojQueAvanza(new DateTimeOffset(Desde.AddDays(1)), TimeSpan.Zero));
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
        public Guid? DocumentoQueFallaSiempre { get; set; }
        public Action? AlConciliar { get; init; }
        public Action? AlBuscar { get; init; }
        public int Ambitos { get; private set; }

        public IConciliacionGestionesAutomaticasService NuevoAmbito()
        {
            Ambitos++;
            return new Ambito(this);
        }

        private sealed class Ambito(ConciliacionFalsa mundo) : IConciliacionGestionesAutomaticasService
        {
            public Task<IReadOnlyList<Guid>> BuscarDocumentosSinGestionAsync(DateTime desdeUtc, CancellationToken cancellationToken = default)
            {
                var tenant = AmbitoTenantExplicito.TenantIdActual ?? throw new InvalidOperationException("Sin Tenant en curso.");
                mundo.Busquedas.Add((tenant, desdeUtc));
                mundo.AlBuscar?.Invoke();
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
                    // Falla una sola vez: dividirlo lo recupera.
                    mundo.LoteQueFalla = null;
                    throw new InvalidOperationException("Choque con el índice único.");
                }
                if (mundo.DocumentoQueFallaSiempre is { } venenoso && documentoIds.Contains(venenoso))
                    throw new InvalidOperationException("Dato que no cumple una restricción.");
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

    /// <summary>Cada consulta de la hora la adelanta <paramref name="paso"/>; las esperas siguen siendo las del sistema.</summary>
    private sealed class RelojQueAvanza(DateTimeOffset inicio, TimeSpan paso) : TimeProvider
    {
        private int _consultas;

        public override DateTimeOffset GetUtcNow() => inicio + paso * _consultas++;
    }
}
