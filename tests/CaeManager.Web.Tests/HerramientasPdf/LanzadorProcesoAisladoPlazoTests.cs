using CaeManager.Infrastructure.HerramientasPdf;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// La lectura de la salida del hijo no se espera más allá del plazo aunque no atienda la cancelación: es lo que hace que el
/// lanzador vuelva (y quien llama suelte su turno) cuando el hijo no se pudo matar y su tubería sigue abierta. Con un
/// proceso real no se puede provocar que falle <c>Kill</c>; aquí la tubería es un flujo que nunca responde.
/// </summary>
public sealed class LanzadorProcesoAisladoPlazoTests
{
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
