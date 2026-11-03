using Microsoft.AspNetCore.Components.Forms;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="IBrowserFile"/> de prueba para las lecturas que reservan del presupuesto de memoria de documentos
/// (<c>ArchivoSubidoReservado</c>): deja declarar un tamaño distinto del real, observar el momento en que se abre el flujo
/// (es cuando la reserva ya tiene que existir) y retener la lectura para solapar dos selecciones.
/// </summary>
internal sealed class ArchivoNavegadorFalso(string nombre, byte[] contenido) : IBrowserFile
{
    /// <summary>Tamaño que declara el navegador; por defecto, el real.</summary>
    public long? TamanoDeclarado { get; init; }

    /// <summary>Se ejecuta cada vez que se abre el flujo, antes de devolverlo.</summary>
    public Action? AlAbrir { get; init; }

    /// <summary>Si no es nulo, el flujo no devuelve ni un byte hasta que se complete.</summary>
    public TaskCompletionSource? Retener { get; init; }

    public int Aperturas { get; private set; }

    public string Name => nombre;

    public DateTimeOffset LastModified => DateTimeOffset.UtcNow;

    public long Size => TamanoDeclarado ?? contenido.Length;

    public string ContentType => "application/pdf";

    public Stream OpenReadStream(long maxAllowedSize = 512000, CancellationToken cancellationToken = default)
    {
        Aperturas++;
        AlAbrir?.Invoke();
        return new FlujoRetenible(new MemoryStream(contenido), Retener);
    }

    private sealed class FlujoRetenible(MemoryStream interno, TaskCompletionSource? retener) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => interno.Read(buffer, offset, count);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (retener is not null)
                await retener.Task.WaitAsync(cancellationToken);
            return await interno.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                interno.Dispose();
            base.Dispose(disposing);
        }
    }
}
