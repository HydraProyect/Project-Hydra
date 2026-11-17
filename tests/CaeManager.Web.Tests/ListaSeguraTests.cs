using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El doble del mediador de los tests de componentes se lee desde el hilo del test mientras el componente
/// sigue enviando desde el dispatcher del renderizador (#2312).
/// </summary>
public sealed class ListaSeguraTests
{
    [Fact]
    public async Task Enumerar_con_OfType_mientras_otro_hilo_escribe_no_lanza_Collection_was_modified()
    {
        var enviadas = new ListaSegura<object>();
        using var parar = new CancellationTokenSource();

        var escritor = Task.Run(() =>
        {
            while (!parar.IsCancellationRequested)
            {
                enviadas.Add(1);
                if (enviadas.Count > 5_000) enviadas.Clear();
            }
        });

        // Sin esperar al escritor, un runner cargado enumeraría una lista vacía y el test pasaría sin ejercitar la carrera.
        SpinWait.SpinUntil(() => enviadas.Count > 0, TimeSpan.FromSeconds(10)).Should().BeTrue();

        var fallos = new List<Exception>();
        for (var i = 0; i < 2_000; i++)
        {
            try
            {
                _ = enviadas.OfType<int>().Count();
            }
            catch (Exception ex)
            {
                fallos.Add(ex);
            }
        }

        await parar.CancelAsync();
        await escritor;

        fallos.Should().BeEmpty("cada enumeración recorre una copia tomada bajo cerrojo");
    }

    [Fact]
    public void Una_enumeracion_en_curso_no_ve_lo_enviado_despues_de_empezar()
    {
        var enviadas = new ListaSegura<object>();
        enviadas.Add("a");

        using var enumerador = enviadas.GetEnumerator();
        enviadas.Add("b");

        enumerador.MoveNext().Should().BeTrue();
        enumerador.Current.Should().Be("a");
        enumerador.MoveNext().Should().BeFalse("la copia se tomó antes de la segunda inserción");
        enviadas.Count.Should().Be(2);
        enviadas[1].Should().Be("b");
    }

    [Fact]
    public void Clear_vacia_y_FindIndex_busca_sobre_lo_enviado()
    {
        var enviadas = new ListaSegura<object> { "x", 2, "y" };

        enviadas.FindIndex(p => p is int).Should().Be(1);
        enviadas.FindLastIndex(p => p is string).Should().Be(2);

        enviadas.Clear();

        enviadas.Count.Should().Be(0);
    }
}
