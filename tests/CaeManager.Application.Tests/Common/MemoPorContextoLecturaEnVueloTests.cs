using CaeManager.Application.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Una sola lectura en vuelo por clave en <see cref="MemoPorContexto{TValor}"/>: las islas del layout piden lo mismo
/// a la vez y el memo solo guardaba el resultado cuando terminaba la primera. Fija que las llamadas simultáneas con
/// la misma clave comparten el cálculo, que otra clave no lo comparte, que ni un fallo ni una cancelación quedan
/// guardados, que la cancelación de quien se une no cancela a los demás y que una llamada anidada con la misma
/// clave no se espera a sí misma.
/// </summary>
public class MemoPorContextoLecturaEnVueloTests
{
    private static readonly TimeSpan Tope = TimeSpan.FromSeconds(5);

    private static readonly ClaveContextoMemo Clave =
        new(Guid.NewGuid(), Guid.NewGuid(), "GestorCae", Guid.NewGuid(), null);

    private sealed class MemoDePrueba(TimeProvider? reloj = null) : MemoPorContexto<string>(reloj);

    /// <summary>Reloj monotónico que solo avanza cuando el test lo pide.</summary>
    private sealed class RelojManual : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Avanzar(TimeSpan intervalo) => _ticks += intervalo.Ticks;
    }

    private static TaskCompletionSource<string> Pendiente() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Dos_llamadas_simultaneas_con_la_misma_clave_calculan_una_sola_vez()
    {
        var memo = new MemoDePrueba();
        var lectura = Pendiente();
        var veces = 0;
        Task<string> Calcular(CancellationToken _) { veces++; return lectura.Task; }

        var primera = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        var segunda = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        segunda.IsCompleted.Should().BeFalse("espera la lectura en vuelo en vez de lanzar la suya");

        lectura.SetResult("valor");

        (await primera.WaitAsync(Tope)).Should().Be("valor");
        (await segunda.WaitAsync(Tope)).Should().BeSameAs(await primera);
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("valor");
        veces.Should().Be(1);
    }

    [Fact]
    public async Task Otra_clave_en_vuelo_a_la_vez_no_comparte_el_calculo()
    {
        var memo = new MemoDePrueba();
        var otroTenant = Clave with { TenantEfectivo = Guid.NewGuid() };
        var deClave = Pendiente();
        var deOtra = Pendiente();
        var veces = 0;

        var primera = memo.ObtenerOCalcularAsync(Clave, _ => { veces++; return deClave.Task; }, CancellationToken.None);
        var segunda = memo.ObtenerOCalcularAsync(otroTenant, _ => { veces++; return deOtra.Task; }, CancellationToken.None);
        veces.Should().Be(2, "otro Tenant efectivo es otro contexto y se lee con el suyo");

        deOtra.SetResult("del otro Tenant");
        deClave.SetResult("del Tenant activo");

        (await primera.WaitAsync(Tope)).Should().Be("del Tenant activo");
        (await segunda.WaitAsync(Tope)).Should().Be("del otro Tenant");
    }

    [Fact]
    public async Task Un_fallo_compartido_llega_a_todos_y_no_se_guarda()
    {
        var memo = new MemoDePrueba();
        var lectura = Pendiente();
        var veces = 0;
        Task<string> Calcular(CancellationToken _) =>
            ++veces == 1 ? lectura.Task : Task.FromResult("recuperado");

        var primera = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        var segunda = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        lectura.SetException(new InvalidOperationException("fallo de lectura"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => primera.WaitAsync(Tope));
        await Assert.ThrowsAsync<InvalidOperationException>(() => segunda.WaitAsync(Tope));
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("recuperado");
        veces.Should().Be(2, "el fallo no quedó guardado: la llamada siguiente vuelve a leer");
    }

    [Fact]
    public async Task Si_se_cancela_quien_se_unio_solo_se_va_el_y_la_lectura_compartida_sigue()
    {
        var memo = new MemoDePrueba();
        var lectura = Pendiente();
        var veces = 0;
        CancellationToken tokenDelCalculo = default;
        Task<string> Calcular(CancellationToken ct) { veces++; tokenDelCalculo = ct; return lectura.Task; }

        using var cancelacionDeLaSegunda = new CancellationTokenSource();
        var primera = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        var segunda = memo.ObtenerOCalcularAsync(Clave, Calcular, cancelacionDeLaSegunda.Token);

        await cancelacionDeLaSegunda.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => segunda.WaitAsync(Tope));
        tokenDelCalculo.IsCancellationRequested.Should().BeFalse("el cálculo corre con el token de quien lo empezó");

        lectura.SetResult("valor");
        (await primera.WaitAsync(Tope)).Should().Be("valor");
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("valor");
        veces.Should().Be(1, "la lectura compartida terminó y quedó guardada");
    }

    [Fact]
    public async Task Si_se_cancela_quien_empezo_quien_se_unio_vuelve_a_leer_por_su_cuenta()
    {
        var memo = new MemoDePrueba();
        var veces = 0;
        async Task<string> Bloqueado(CancellationToken ct)
        {
            veces++;
            await Task.Delay(Timeout.Infinite, ct);
            return "nunca";
        }
        Task<string> Propio(CancellationToken _) { veces++; return Task.FromResult("de la segunda"); }

        using var cancelacionDeLaPrimera = new CancellationTokenSource();
        var primera = memo.ObtenerOCalcularAsync(Clave, Bloqueado, cancelacionDeLaPrimera.Token);
        var segunda = memo.ObtenerOCalcularAsync(Clave, Propio, CancellationToken.None);
        veces.Should().Be(1, "la segunda se unió a la lectura en vuelo");

        await cancelacionDeLaPrimera.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => primera.WaitAsync(Tope));
        (await segunda.WaitAsync(Tope)).Should().Be("de la segunda", "una cancelación ajena no la cancela");
        (await memo.ObtenerOCalcularAsync(Clave, Propio, CancellationToken.None)).Should().Be("de la segunda");
        veces.Should().Be(2, "la cancelada no quedó guardada y lo que leyó la segunda sí");
    }

    [Fact]
    public async Task Una_llamada_anidada_con_la_misma_clave_no_se_espera_a_si_misma()
    {
        var memo = new MemoDePrueba();
        var veces = 0;
        async Task<string> Exterior(CancellationToken ct)
        {
            veces++;
            var interior = await memo.ObtenerOCalcularAsync(
                Clave, _ => { veces++; return Task.FromResult("interior"); }, ct);
            return $"exterior con {interior}";
        }

        (await memo.ObtenerOCalcularAsync(Clave, Exterior, CancellationToken.None).WaitAsync(Tope))
            .Should().Be("exterior con interior");
        veces.Should().Be(2);
        (await memo.ObtenerOCalcularAsync(Clave, Exterior, CancellationToken.None))
            .Should().Be("exterior con interior", "lo guardado es lo del cálculo exterior");
        veces.Should().Be(2);
    }

    [Fact]
    public async Task Si_la_cancelacion_de_quien_empezo_llega_como_otra_excepcion_quien_se_unio_reintenta()
    {
        var memo = new MemoDePrueba();
        var veces = 0;
        async Task<string> FallaAlCancelar(CancellationToken ct)
        {
            veces++;
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { throw new ObjectDisposedException("conexión"); }
            return "nunca";
        }
        Task<string> Propio(CancellationToken _) { veces++; return Task.FromResult("de la segunda"); }

        using var cancelacionDeLaPrimera = new CancellationTokenSource();
        var primera = memo.ObtenerOCalcularAsync(Clave, FallaAlCancelar, cancelacionDeLaPrimera.Token);
        var segunda = memo.ObtenerOCalcularAsync(Clave, Propio, CancellationToken.None);

        await cancelacionDeLaPrimera.CancelAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => primera.WaitAsync(Tope));
        (await segunda.WaitAsync(Tope)).Should().Be("de la segunda");
        veces.Should().Be(2);
    }

    [Fact]
    public async Task Una_tarea_hija_del_calculo_con_la_misma_clave_no_se_espera_a_su_llamante()
    {
        var memo = new MemoDePrueba();
        async Task<string> Exterior(CancellationToken ct)
        {
            var hijas = await Task.WhenAll(
                Task.Run(() => memo.ObtenerOCalcularAsync(Clave, _ => Task.FromResult("hija"), ct), ct),
                Task.Run(() => memo.ObtenerOCalcularAsync(Clave, _ => Task.FromResult("hija"), ct), ct));
            return string.Join("+", hijas);
        }

        (await memo.ObtenerOCalcularAsync(Clave, Exterior, CancellationToken.None).WaitAsync(Tope))
            .Should().Be("hija+hija");
    }

    private sealed class OtroMemoDePrueba : MemoPorContexto<string>;

    /// <summary>
    /// Dos memos que se piden uno al otro desde flujos hermanos dentro de la misma puerta reentrante: si cada
    /// cálculo se uniera al del otro, se esperarían para siempre.
    /// </summary>
    [Fact]
    public async Task Dos_memos_que_se_piden_uno_al_otro_desde_flujos_hermanos_no_se_interbloquean()
    {
        var puerta = new PuertaAccesoDatos();
        var memoA = new MemoDePrueba();
        var memoB = new OtroMemoDePrueba();
        var ambosEnVuelo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ambosPidieron = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var empezados = 0;
        var pedidos = 0;

        static async Task Barrera(TaskCompletionSource barrera, Func<int> llegar)
        {
            if (llegar() == 2) barrera.SetResult();
            await barrera.Task;
        }

        // Las dos lecturas cruzadas solo terminan cuando ambas han empezado: con los dos cálculos exteriores aún en
        // vuelo, unirse al del otro sería esperar para siempre.
        Func<CancellationToken, Task<string>> Cruzada(string valor) => async _ =>
        {
            await Barrera(ambosPidieron, () => Interlocked.Increment(ref pedidos));
            return valor;
        };

        async Task<string> CalcularA(CancellationToken ct)
        {
            await Barrera(ambosEnVuelo, () => Interlocked.Increment(ref empezados));
            var b = await puerta.EjecutarAsync(() => memoB.ObtenerOCalcularAsync(Clave, Cruzada("B desde A"), ct), ct);
            return $"A con {b}";
        }

        async Task<string> CalcularB(CancellationToken ct)
        {
            await Barrera(ambosEnVuelo, () => Interlocked.Increment(ref empezados));
            var a = await puerta.EjecutarAsync(() => memoA.ObtenerOCalcularAsync(Clave, Cruzada("A desde B"), ct), ct);
            return $"B con {a}";
        }

        var resultados = await puerta.EjecutarAsync(() => Task.WhenAll(
            puerta.EjecutarAsync(() => memoA.ObtenerOCalcularAsync(Clave, CalcularA, CancellationToken.None)),
            puerta.EjecutarAsync(() => memoB.ObtenerOCalcularAsync(Clave, CalcularB, CancellationToken.None))))
            .WaitAsync(Tope);

        resultados.Should().Equal("A con B desde A", "B con A desde B");
        (await memoA.ObtenerOCalcularAsync(Clave, CalcularA, CancellationToken.None)).Should().Be("A con B desde A");
        (await memoB.ObtenerOCalcularAsync(Clave, CalcularB, CancellationToken.None)).Should().Be("B con A desde B");
    }

    [Fact]
    public async Task Un_fallo_anterior_a_Invalidar_no_retira_la_entrada_posterior()
    {
        var memo = new MemoDePrueba();
        var anterior = Pendiente();
        var veces = 0;
        Task<string> Calcular(CancellationToken _) =>
            ++veces == 1 ? anterior.Task : Task.FromResult("posterior");

        var primera = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        memo.Invalidar();
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("posterior");

        anterior.SetException(new InvalidOperationException("fallo de la lectura anterior"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => primera.WaitAsync(Tope));

        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("posterior");
        veces.Should().Be(2, "el fallo del cálculo viejo no retira la entrada del nuevo");
    }

    [Fact]
    public async Task Invalidar_con_una_lectura_en_vuelo_hace_que_la_llamada_siguiente_no_se_una_a_ella()
    {
        var memo = new MemoDePrueba();
        var anterior = Pendiente();
        var veces = 0;
        Task<string> Calcular(CancellationToken _) =>
            ++veces == 1 ? anterior.Task : Task.FromResult("posterior a la escritura");

        var primera = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        memo.Invalidar();
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None))
            .Should().Be("posterior a la escritura");

        anterior.SetResult("anterior a la escritura");
        (await primera.WaitAsync(Tope)).Should().Be("anterior a la escritura");
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None))
            .Should().Be("posterior a la escritura");
        veces.Should().Be(2);
    }

    [Fact]
    public async Task Una_lectura_en_vuelo_desde_hace_diez_segundos_ya_no_se_comparte()
    {
        var reloj = new RelojManual();
        var memo = new MemoDePrueba(reloj);
        var colgada = Pendiente();
        var veces = 0;
        Task<string> Calcular(CancellationToken _) =>
            ++veces == 1 ? colgada.Task : Task.FromResult("nueva");

        var primera = memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None);
        reloj.Avanzar(MemoPorContexto.Caducidad);

        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("nueva");
        veces.Should().Be(2, "la caducidad cuenta desde que empezó el cálculo, también si sigue en vuelo");

        colgada.SetResult("vieja");
        (await primera.WaitAsync(Tope)).Should().Be("vieja");
        (await memo.ObtenerOCalcularAsync(Clave, Calcular, CancellationToken.None)).Should().Be("nueva");
    }
}
