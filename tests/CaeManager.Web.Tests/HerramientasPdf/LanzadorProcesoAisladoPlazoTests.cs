using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// La lectura de la salida del hijo no se espera más allá del plazo aunque no atienda la cancelación: es lo que hace que el
/// lanzador vuelva (y quien llama sepa si puede soltar su turno) cuando el hijo no se pudo matar y su tubería sigue abierta.
/// Con un proceso real no se puede provocar que falle <c>Kill</c>; aquí la tubería es un flujo que nunca responde, y la
/// confirmación de la muerte se prueba con dobles de sus pasos.
/// </summary>
public sealed class LanzadorProcesoAisladoPlazoTests
{
    private static readonly TimeSpan Margen = TimeSpan.FromMilliseconds(50);

    [Fact]
    public async Task Un_hijo_que_ya_salio_consta_terminado_sin_matarlo()
    {
        var matado = false;

        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => true, () => matado = true, _ => Task.CompletedTask, Margen, _ => { });

        terminado.Should().BeTrue();
        matado.Should().BeFalse();
    }

    [Fact]
    public async Task Un_hijo_que_se_mata_y_sale_en_el_margen_consta_terminado()
    {
        var matado = false;

        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => false, () => matado = true, _ => Task.CompletedTask, Margen, _ => { });

        terminado.Should().BeTrue();
        matado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_Kill_falla_y_el_hijo_no_sale_no_consta_terminado_y_se_registra_el_fallo_al_matar()
    {
        Exception? registrado = null;

        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => false, () => throw new InvalidOperationException("sin permiso"), token => Task.Delay(Timeout.Infinite, token),
            Margen, ex => registrado = ex).WaitAsync(TimeSpan.FromSeconds(5));

        terminado.Should().BeFalse();
        registrado.Should().BeOfType<InvalidOperationException>("el motivo útil es el fallo al matar, no la espera vencida");
    }

    [Fact]
    public async Task Si_Kill_falla_porque_el_hijo_ya_estaba_saliendo_consta_terminado()
    {
        // La carrera del plazo: el callback ya mató al hijo y el segundo Kill lanza mientras sale. Cuenta la salida.
        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => false, () => throw new InvalidOperationException("ya salió"), _ => Task.CompletedTask, Margen, _ => { });

        terminado.Should().BeTrue();
    }

    [Fact]
    public async Task Si_el_hijo_no_sale_en_el_margen_tras_matarlo_no_consta_terminado()
    {
        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => false, () => { }, token => Task.Delay(Timeout.Infinite, token), Margen, _ => { })
            .WaitAsync(TimeSpan.FromSeconds(5));

        terminado.Should().BeFalse();
    }

    [Fact]
    public async Task Si_el_registro_falla_al_no_confirmar_la_muerte_sigue_sin_constar_terminado_y_no_lanza()
    {
        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => false, () => throw new InvalidOperationException("sin permiso"), token => Task.Delay(Timeout.Infinite, token),
            Margen, _ => throw new InvalidOperationException("el proveedor de registro falla")).WaitAsync(TimeSpan.FromSeconds(5));

        terminado.Should().BeFalse("un fallo del registro no puede convertirse en una excepción que suelte el turno");
    }

    [Fact]
    public async Task Si_comprobar_la_salida_lanza_no_consta_terminado()
    {
        var terminado = await LanzadorProcesoAislado.ConfirmarTerminadoAsync(
            () => throw new InvalidOperationException("sin proceso asociado"), () => { }, _ => Task.CompletedTask, Margen, _ => { });

        terminado.Should().BeFalse();
    }

    [Fact]
    public async Task Una_tarea_de_tuberia_que_no_termina_en_su_espera_se_abandona_y_lo_dice()
    {
        var nunca = new TaskCompletionSource();

        (await LanzadorProcesoAislado.TerminaEnAsync(nunca.Task, Margen).WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();
        (await LanzadorProcesoAislado.TerminaEnAsync(Task.FromException(new IOException("tubería rota")), Margen)).Should()
            .BeTrue("terminó, aunque fuera con excepción");
    }

    [Fact]
    public async Task Una_tarea_de_tuberia_abandonada_que_falla_despues_queda_observada()
    {
        var sinObservar = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> anotar = (_, e) =>
        {
            if (e.Exception.InnerExceptions.Any(ex => ex.Message == MarcaFalloTardio))
                lock (sinObservar)
                    sinObservar.Add(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += anotar;
        try
        {
            await AbandonarYFallarDespuesAsync();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            lock (sinObservar)
                sinObservar.Should().BeEmpty("la continuación del abandono observa la excepción tardía");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= anotar;
        }
    }

    private const string MarcaFalloTardio = "fallo tardío de una tubería abandonada";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static async Task AbandonarYFallarDespuesAsync()
    {
        var tarde = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (await LanzadorProcesoAislado.TerminaEnAsync(tarde.Task, Margen)).Should().BeFalse();
        tarde.SetException(new IOException(MarcaFalloTardio));
        // Que la continuación llegue a correr antes de soltar la tarea.
        await Task.Delay(100);
    }

    [Fact]
    public async Task Una_lectura_bloqueada_que_no_atiende_la_cancelacion_no_retiene_al_lanzador_mas_alla_del_plazo()
    {
        using var tuberia = new TuberiaQueNoResponde();
        using var plazo = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var lectura = LanzadorProcesoAislado.LeerConTopeAsync(tuberia, 1024, plazo.Token);

        var esperar = () => LanzadorProcesoAislado.HastaElPlazoAsync(lectura, plazo.Token).WaitAsync(TimeSpan.FromSeconds(5));

        await esperar.Should().ThrowAsync<OperationCanceledException>("vence el plazo; un TimeoutException diría que se quedó esperando a la lectura");
        lectura.IsCompleted.Should().BeFalse("la lectura sigue bloqueada: se abandona, no se espera");
    }

    [Fact]
    public async Task Una_lectura_que_termina_antes_del_plazo_devuelve_lo_leido()
    {
        using var origen = new MemoryStream("%PDF-1.4 x"u8.ToArray());

        var salida = await LanzadorProcesoAislado.HastaElPlazoAsync(
            LanzadorProcesoAislado.LeerConTopeAsync(origen, 1024, CancellationToken.None), CancellationToken.None);

        salida!.Value.ToArray().Should().Equal("%PDF-1.4 x"u8.ToArray());
    }

    /// <summary>Una tubería cuyo otro extremo sigue abierto y no escribe: la lectura no vuelve nunca, ni al cancelar.</summary>
    private sealed class TuberiaQueNoResponde : Stream
    {
        private readonly TaskCompletionSource<int> _nunca = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(_nunca.Task);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
