using CaeManager.Application.Common;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Seguimiento de S-C-3 (OWASP API4:2023, ASVS 4.0.3 V12.1.1, CWE-400): el presupuesto por
/// proceso de bytes de documento en memoria reserva antes de cargar, devuelve una sola vez al
/// desechar (también cuando el trabajo falla), espera acotada y en orden, y al vencer la espera
/// rechaza sin quedarse con nada.
/// </summary>
public class PresupuestoMemoriaDocumentosTests
{
    private const long Maximo = 1000;

    private static PresupuestoMemoriaDocumentos Crear(TimeSpan? espera = null, TimeProvider? reloj = null) =>
        new(Options.Create(new OpcionesPresupuestoMemoriaDocumentos
        {
            MaximoBytes = Maximo,
            EsperaMaxima = espera ?? TimeSpan.FromSeconds(10)
        }), reloj ?? TimeProvider.System);

    [Fact]
    public async Task Reservar_descuenta_y_desechar_devuelve_una_sola_vez()
    {
        var presupuesto = Crear();

        var reserva = await presupuesto.ReservarAsync(300);
        var otra = await presupuesto.ReservarAsync(200);
        presupuesto.Reservado.Should().Be(500);

        reserva.Dispose();
        reserva.Dispose();

        presupuesto.Reservado.Should().Be(200, "desechar dos veces no devuelve dos veces");
        otra.Dispose();
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task La_reserva_se_devuelve_aunque_el_trabajo_que_protege_falle()
    {
        var presupuesto = Crear();

        var trabajo = async () =>
        {
            using var reserva = await presupuesto.ReservarAsync(Maximo);
            presupuesto.Reservado.Should().Be(Maximo, "control: la reserva estaba tomada");
            throw new InvalidOperationException("fallo al convertir");
        };

        await trabajo.Should().ThrowExactlyAsync<InvalidOperationException>();
        presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_hueco_espera_lo_acotado_y_despues_rechaza_sin_quedarse_nada()
    {
        var presupuesto = Crear(TimeSpan.FromMilliseconds(100));
        using var ocupado = await presupuesto.ReservarAsync(800);

        var reservar = async () => await presupuesto.ReservarAsync(300);

        await reservar.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        presupuesto.EnEspera.Should().Be(0, "la espera vencida sale de la cola");
        presupuesto.Reservado.Should().Be(800, "el rechazo no descuenta nada");
    }

    [Fact]
    public async Task Una_espera_se_concede_en_cuanto_otra_reserva_devuelve_su_hueco()
    {
        var presupuesto = Crear();
        var ocupado = await presupuesto.ReservarAsync(800);

        var esperando = presupuesto.ReservarAsync(300).AsTask();
        await Task.Delay(50);
        esperando.IsCompleted.Should().BeFalse("control: no cabe mientras el otro lo retiene");
        presupuesto.EnEspera.Should().Be(1);

        ocupado.Dispose();

        using var concedida = await esperando.WaitAsync(TimeSpan.FromSeconds(5));
        presupuesto.Reservado.Should().Be(300);
        presupuesto.EnEspera.Should().Be(0);
    }

    [Fact]
    public async Task Las_esperas_se_conceden_en_orden_y_una_pequena_no_adelanta_a_una_grande()
    {
        var presupuesto = Crear();
        var ocupado = await presupuesto.ReservarAsync(600);

        var grande = presupuesto.ReservarAsync(700).AsTask();
        var pequena = presupuesto.ReservarAsync(100).AsTask();
        await Task.Delay(50);

        pequena.IsCompleted.Should().BeFalse("cabría, pero hay una espera anterior en la cola");
        grande.IsCompleted.Should().BeFalse();

        ocupado.Dispose();
        using var reservaGrande = await grande.WaitAsync(TimeSpan.FromSeconds(5));
        using var reservaPequena = await pequena.WaitAsync(TimeSpan.FromSeconds(5));
        presupuesto.Reservado.Should().Be(800);
    }

    [Fact]
    public async Task Al_vencer_la_primera_de_la_cola_se_conceden_las_que_esperaban_detras()
    {
        // Reloj manual: solo vence la espera de la grande; la de la pequeña no vence
        // nunca, así que solo puede salir por la concesión que deja libre la grande.
        var reloj = new RelojManual();
        var presupuesto = Crear(TimeSpan.FromSeconds(10), reloj);
        using var ocupado = await presupuesto.ReservarAsync(600);

        var grande = presupuesto.ReservarAsync(700).AsTask();
        var pequena = presupuesto.ReservarAsync(100).AsTask();
        reloj.Temporizadores.Should().Be(2, "control: cada espera arma su vencimiento");
        pequena.IsCompleted.Should().BeFalse("control: cabría, pero espera detrás de la grande");

        reloj.Vencer(0);

        await grande.Awaiting(t => t).Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        using var reservaPequena = await pequena.WaitAsync(TimeSpan.FromSeconds(5));
        presupuesto.Reservado.Should().Be(700);
    }

    [Fact]
    public async Task Una_peticion_mayor_que_el_presupuesto_espera_a_tenerlo_entero_para_ella_sola()
    {
        var presupuesto = Crear();
        var ocupado = await presupuesto.ReservarAsync(1);

        var enorme = presupuesto.ReservarAsync(Maximo * 5).AsTask();
        await Task.Delay(50);
        enorme.IsCompleted.Should().BeFalse("control: con algo reservado no le cabe");

        ocupado.Dispose();
        using var reserva = await enorme.WaitAsync(TimeSpan.FromSeconds(5));

        reserva.Bytes.Should().Be(Maximo);
        presupuesto.Reservado.Should().Be(Maximo, "descuenta el presupuesto entero, nunca más");
    }

    [Fact]
    public async Task Cancelar_la_espera_la_saca_de_la_cola_sin_descontar()
    {
        var presupuesto = Crear();
        using var ocupado = await presupuesto.ReservarAsync(Maximo);
        using var cancelacion = new CancellationTokenSource();

        var esperando = presupuesto.ReservarAsync(10, cancelacion.Token).AsTask();
        await Task.Delay(20);
        await cancelacion.CancelAsync();

        await esperando.Awaiting(t => t).Should().ThrowAsync<OperationCanceledException>();
        presupuesto.EnEspera.Should().Be(0);
        presupuesto.Reservado.Should().Be(Maximo);
    }

    [Fact]
    public async Task Muchas_reservas_concurrentes_nunca_pasan_del_presupuesto_y_lo_devuelven_entero()
    {
        var presupuesto = Crear();
        var maximoVisto = 0L;

        var trabajos = Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            using var reserva = await presupuesto.ReservarAsync(100 + (i % 7) * 50);
            var ahora = presupuesto.Reservado;
            InterlockedMax(ref maximoVisto, ahora);
            await Task.Yield();
        }));
        await Task.WhenAll(trabajos).WaitAsync(TimeSpan.FromSeconds(30));

        maximoVisto.Should().BeLessThanOrEqualTo(Maximo).And.BePositive();
        presupuesto.Reservado.Should().Be(0);
        presupuesto.EnEspera.Should().Be(0);
    }

    [Fact]
    public void Los_valores_por_defecto_son_validos_y_un_presupuesto_bajo_el_suelo_no()
    {
        new OpcionesPresupuestoMemoriaDocumentos().SonValidas().Should().BeTrue();
        new OpcionesPresupuestoMemoriaDocumentos().MaximoBytes.Should().Be(512L * 1024 * 1024);
        OpcionesPresupuestoMemoriaDocumentos.MinimoBytes.Should().Be(8L * LimitesArchivoSubido.TamanoMaximoBytes);
        new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = OpcionesPresupuestoMemoriaDocumentos.MinimoBytes }.SonValidas().Should().BeTrue();
        new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = OpcionesPresupuestoMemoriaDocumentos.MinimoBytes - 1 }.SonValidas().Should().BeFalse(
            "con menos, una operación sobre un archivo de 10 MiB esperaría a su propia reserva");
        new OpcionesPresupuestoMemoriaDocumentos { EsperaMaxima = TimeSpan.Zero }.SonValidas().Should().BeFalse();
        new OpcionesPresupuestoMemoriaDocumentos { EsperaMaxima = TimeSpan.FromHours(1) }.SonValidas().Should().BeFalse();
    }

    /// <summary>
    /// Reloj que no vence nada solo: cada espera acotada registra su temporizador y el
    /// test decide cuál vence, sin depender de lo rápido que vaya la máquina.
    /// </summary>
    private sealed class RelojManual : TimeProvider
    {
        private readonly List<Temporizador> _temporizadores = [];

        public int Temporizadores
        {
            get { lock (_temporizadores) return _temporizadores.Count; }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var temporizador = new Temporizador(callback, state);
            lock (_temporizadores)
                _temporizadores.Add(temporizador);
            return temporizador;
        }

        public void Vencer(int indice)
        {
            Temporizador temporizador;
            lock (_temporizadores)
                temporizador = _temporizadores[indice];
            temporizador.Disparar();
        }

        private sealed class Temporizador(TimerCallback callback, object? state) : ITimer
        {
            private int _desechado;

            public void Disparar()
            {
                if (Volatile.Read(ref _desechado) == 0)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => Interlocked.Exchange(ref _desechado, 1);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static void InterlockedMax(ref long objetivo, long valor)
    {
        long actual;
        while ((actual = Interlocked.Read(ref objetivo)) < valor
               && Interlocked.CompareExchange(ref objetivo, valor, actual) != actual)
        {
        }
    }
}
