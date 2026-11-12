namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Flujo en memoria que no pasa de una cota: la escritura que la superaría lanza antes de copiar nada, y el búfer crece
/// doblando pero sin pasar de la cota. Así, lo que ocupa a la vez el guardado de un tramo (búfer viejo y nuevo al crecer,
/// o búfer y <see cref="MemoryStream.ToArray"/> al final) no pasa de dos cotas, que es lo que reserva quien extrae.
/// </summary>
internal sealed class FlujoMemoriaAcotado(long cota) : MemoryStream
{
    /// <summary>Alguna escritura habría pasado la cota; lo que haya en el flujo no es un tramo completo.</summary>
    public bool CotaSuperada { get; private set; }

    public long Cota => cota;

    public override void Write(byte[] buffer, int offset, int count)
    {
        AsegurarCabida(Position + count);
        base.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        AsegurarCabida(Position + buffer.Length);
        base.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        AsegurarCabida(Position + 1);
        base.WriteByte(value);
    }

    public override void SetLength(long value)
    {
        AsegurarCabida(value);
        base.SetLength(value);
    }

    private void AsegurarCabida(long necesario)
    {
        if (necesario > cota)
        {
            CotaSuperada = true;
            throw new CotaFlujoSuperadaException();
        }

        if (necesario <= Capacity)
            return;

        // Se fija la capacidad aquí para que MemoryStream no la doble por su cuenta por encima de la cota.
        var tope = Math.Min(cota, Array.MaxLength);
        Capacity = (int)Math.Min(Math.Max(necesario, 2L * Capacity), tope);
    }
}

/// <summary>Una escritura en <see cref="FlujoMemoriaAcotado"/> habría pasado su cota.</summary>
internal sealed class CotaFlujoSuperadaException() : IOException("El tramo supera el tamaño que se puede preparar.");
