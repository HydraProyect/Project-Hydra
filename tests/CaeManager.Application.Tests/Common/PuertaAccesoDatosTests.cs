using CaeManager.Application.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Cubre el invariante que <c>PuertaAccesoDatos</c> existe para garantizar
/// (ver revert de P1-11 en ROADMAP.md, PR #47) — nunca lo tuvo antes porque
/// P1-11 introdujo el único test que la ejercitaba, y se borró junto con el
/// diseño que probaba al revertir.
/// </summary>
public class PuertaAccesoDatosTests
{
    [Fact]
    public async Task Operaciones_no_anidadas_se_serializan()
    {
        var puerta = new PuertaAccesoDatos();
        var enVuelo = 0;
        var huboSolape = false;

        async Task<int> Operacion()
        {
            if (Interlocked.Increment(ref enVuelo) > 1) huboSolape = true;
            await Task.Delay(20);
            Interlocked.Decrement(ref enVuelo);
            return 0;
        }

        await Task.WhenAll(
            puerta.EjecutarAsync(Operacion),
            puerta.EjecutarAsync(Operacion),
            puerta.EjecutarAsync(Operacion));

        huboSolape.Should().BeFalse();
    }

    [Fact]
    public async Task Una_operacion_anidada_no_se_bloquea_a_si_misma()
    {
        // Si la reentrancia fallara, el await interno se quedaría esperando
        // la puerta que el flujo externo ya tiene tomada: el test colgaría en
        // vez de fallar por aserción — mismo patrón que ObtenerKpisGlobalesQuery,
        // que despacha un Send anidado dentro de otro.
        var puerta = new PuertaAccesoDatos();

        var resultado = await puerta.EjecutarAsync(() => puerta.EjecutarAsync(() => Task.FromResult(42)));

        resultado.Should().Be(42);
    }

    [Fact]
    public async Task Dentro_de_un_flujo_reentrante_dos_hijas_en_paralelo_pueden_solaparse()
    {
        // Limitación conocida, no una regresión de este test (hallazgo de la
        // auditoría independiente del revert de P1-11, PR #47, 2026-08-01):
        // el AsyncLocal de reentrancia fluye a TODA tarea hija lanzada dentro
        // de un flujo que ya tiene la puerta, así que un Task.WhenAll de hijas
        // *dentro* de un flujo reentrante se salta el semáforo entre ellas.
        //
        // No se dispara hoy: ObtenerKpisGlobalesQuery itera con foreach
        // secuencial, y ningún validador de FluentValidation del repo es
        // async (nada de MustAsync/CustomAsync). Si algún día un Send paralelo
        // o un validador async se cuela dentro de un flujo ya reentrante, este
        // test es la prueba de que la carrera reaparece — y si algún día deja
        // de ser cierto (huboSolape empieza a dar false), el diseño cambió y
        // la nota de ROADMAP.md hay que actualizarla, no borrar este test.
        var puerta = new PuertaAccesoDatos();
        var enVuelo = 0;
        var huboSolape = false;

        async Task Hija()
        {
            if (Interlocked.Increment(ref enVuelo) > 1) huboSolape = true;
            await Task.Delay(20);
            Interlocked.Decrement(ref enVuelo);
        }

        await puerta.EjecutarAsync(() => Task.WhenAll(
            puerta.EjecutarAsync(Hija),
            puerta.EjecutarAsync(Hija)));

        huboSolape.Should().BeTrue(
            "es la limitación conocida documentada en ROADMAP.md — si empieza a dar false, el diseño cambió");
    }

    [Fact]
    public void No_implementa_IDisposable_a_proposito()
    {
        // Ver el comentario de clase: versiones anteriores SÍ implementaban
        // IDisposable y disponían el semáforo al terminar el scope — eso fue
        // la causa raíz de tres incidentes reales en producción (Sentry
        // DOTNET-2, DOTNET-5, DOTNET-6). SemaphoreSlim.Dispose() concurrente
        // con WaitAsync/Release no es un uso soportado por la BCL y, bajo esa
        // carrera concreta, ni un timeout compuesto por fuera ni la
        // cancelación nativa de WaitAsync(CancellationToken) rescatan de
        // forma fiable una espera ya colgada (comprobado en aislamiento con
        // ambas variantes). La puerta no retiene ningún recurso no
        // administrado (nunca toca AvailableWaitHandle), así que no disponer
        // nada es la solución, no un descuido — no reintroducir IDisposable
        // aquí sin releer este comentario.
        typeof(PuertaAccesoDatos).Should().NotBeAssignableTo<IDisposable>();
    }

    [Fact]
    public async Task Una_espera_en_cola_se_sirve_con_normalidad_sin_ninguna_carrera_de_disposicion()
    {
        // Sin Dispose no hay nada que envenenar: cuando A libera, B
        // simplemente entra — sin cuelgues, sin ObjectDisposedException,
        // exista o no todavía alguien esperando el resultado de B.
        var puerta = new PuertaAccesoDatos();
        var entroA = new TaskCompletionSource();
        var liberarA = new TaskCompletionSource();
        var tareaA = puerta.EjecutarAsync(async () =>
        {
            entroA.SetResult();
            await liberarA.Task;
        });

        await entroA.Task; // A ya tiene la puerta.
        var tareaB = puerta.EjecutarAsync(() => Task.CompletedTask); // B queda en cola, de verdad esperando.

        liberarA.SetResult();
        await tareaA;

        var ganadora = await Task.WhenAny(tareaB, Task.Delay(TimeSpan.FromSeconds(5)));
        ganadora.Should().Be(tareaB, "sin Dispose no hay ninguna carrera que pueda colgar la espera de B");
        await tareaB; // no debe lanzar nada
    }

    [Fact]
    public async Task Cancelar_la_espera_justo_antes_de_que_el_ocupante_libere_nunca_ejecuta_la_operacion()
    {
        // SemaphoreSlim.WaitAsync puede conceder el semáforo a un esperador
        // aunque su token ya estuviera cancelado en el instante del Release()
        // de quien lo tenía: es una carrera de la propia BCL entre "cancelar"
        // y "liberar" (confirmada en aislamiento: sin el control explícito de
        // EjecutarAsync tras WaitAsync, ~53% de 20.000 iteraciones de este
        // mismo patrón ejecutaban la operación pese a la cancelación ya
        // solicitada antes de liberar). Sin este test, esa carrera es el
        // flake intermitente de
        // ImportarClientesGen2Tests.Retirar_la_pagina_mientras_el_historial_espera_la_puerta_de_datos_la_saca_de_la_cola
        // bajo la contención de la suite completa. Muchas iteraciones porque
        // es una carrera de timing: una sola pasada no la detectaría de forma
        // fiable en ningún sentido (ni en rojo ni en verde).
        const int iteraciones = 3000;

        for (var i = 0; i < iteraciones; i++)
        {
            var puerta = new PuertaAccesoDatos();
            var ocupante = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cts = new CancellationTokenSource();
            var operacionEjecuto = false;

            var tareaOcupante = puerta.EjecutarAsync(() => ocupante.Task);
            var tareaEsperador = puerta.EjecutarAsync(() => { operacionEjecuto = true; return Task.CompletedTask; }, cts.Token);

            // Deja que el esperador quede realmente encolado en el semáforo
            // antes de cancelar, igual que en el test de bUnit real la página
            // ya está bloqueada en el await cuando el test sigue.
            await Task.Yield();
            await Task.Delay(0);

            cts.Cancel();
            ocupante.SetResult();
            await tareaOcupante;

            try { await tareaEsperador; }
            catch (OperationCanceledException) { }

            operacionEjecuto.Should().BeFalse(
                $"iteración {i}: la espera se canceló antes de que el ocupante liberara la puerta");
        }
    }
}
