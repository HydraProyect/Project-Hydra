using FluentAssertions;

namespace CaeManager.Application.Tests;

/// <summary>
/// Controles del instrumento: los tests que acotan coste con <see cref="TiempoCpuDelHilo"/>
/// solo valen si de verdad cuenta el cálculo y no cuenta la espera.
/// </summary>
public class TiempoCpuDelHiloTests
{
    [Fact]
    public void Un_hilo_que_espera_no_suma_tiempo_de_cpu()
    {
        // Lo que un cronómetro sí cuenta y este instrumento no debe contar: el hilo
        // parado. Medio segundo dormido tiene que dejar la CPU casi quieta.
        var (_, cpu) = TiempoCpuDelHilo.Medir(() =>
        {
            Thread.Sleep(TimeSpan.FromMilliseconds(500));
            return 0;
        });

        cpu.Should().BeLessThan(TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void Un_hilo_que_calcula_suma_tiempo_de_cpu()
    {
        // Si el instrumento no avanzara nunca, todo techo medido con él daría verde.
        // El reloj de pared solo pone un límite al bucle por si no avanza.
        var inicio = TiempoCpuDelHilo.Actual();
        var limite = System.Diagnostics.Stopwatch.StartNew();
        var acumulado = 0L;

        while (TiempoCpuDelHilo.Actual() - inicio < TimeSpan.FromMilliseconds(50)
               && limite.Elapsed < TimeSpan.FromSeconds(60))
        {
            for (var i = 0; i < 100_000; i++)
                acumulado += i ^ acumulado;
        }

        (TiempoCpuDelHilo.Actual() - inicio).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(50),
            "un bucle de cálculo tiene que hacer avanzar el tiempo de CPU del hilo (acumulado {0})", acumulado);
    }
}
