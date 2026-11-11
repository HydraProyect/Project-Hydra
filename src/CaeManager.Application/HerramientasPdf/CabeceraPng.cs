using System.Buffers.Binary;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Lo que la cabecera IHDR de un PNG dice de la memoria que necesitará el decodificador, leído SIN decodificar. Un PNG
/// entrelazado (Adam7) no se puede dibujar fila a fila: libpng, tal como lo usa Skia, entrega las filas de cada pasada
/// salteadas, así que Skia reserva ADEMÁS del bitmap un búfer nativo con TODAS las filas de la imagen en el formato nativo
/// del PNG (<see cref="BytesDeBufferEntrelazado"/>), que llega a 8 bytes por píxel en un RGBA de 16 bits. Ese búfer no es
/// el bitmap decodificado ni se ve en las dimensiones por sí solas (depende del tipo de color y de la profundidad).
///
/// <para>
/// <b>Cálculo.</b> Es una COTA SUPERIOR en el PEOR caso de las transformaciones que libpng puede aplicar a la fila antes de
/// entregarla, porque desde IHDR no se sabe si después viene un tRNS: muestras por píxel tras expandir la transparencia y la
/// paleta (gris 2, porque un tRNS lo convierte en gris con alfa; RGB 4, porque un tRNS lo convierte en RGBA; paleta 4, por si
/// se expande a RGBA; gris con alfa 2; RGBA 4), por 2 bytes si la profundidad es 16 y por 1 en los demás casos (libpng
/// desempaqueta las profundidades menores de 8 a un byte por muestra). Skia m119 no expande la paleta en libpng (lo hace su
/// swizzler), así que para la paleta se reserva de más a propósito. Un PNG sin entrelazar no usa ese búfer: 0.
/// </para>
///
/// <para>
/// <b>Qué no se garantiza.</b> El valor se deduce del código fuente de Skia (m119) y de libpng, no de una medición de
/// memoria del binario desplegado. Se reserva de más antes que de menos, y una cabecera que no se reconoce (métodos de
/// compresión, filtro o entrelazado, tipo de color o profundidad fuera de la especificación) no se presupuesta:
/// <see cref="Leer"/> devuelve null y el procesador la rechaza.
/// </para>
/// </summary>
public sealed record CabeceraPng(int Ancho, int Alto, int ProfundidadBits, int TipoDeColor, bool Entrelazado)
{
    /// <summary>Firma (8) + longitud de IHDR (4) + «IHDR» (4) + los 13 bytes de IHDR (ancho, alto, profundidad, color, compresión, filtro, entrelazado).</summary>
    private const int LongitudMinima = 8 + 4 + 4 + 13;

    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Bytes por píxel de una fila tal como la entrega libpng (el que mide <c>png_get_rowbytes</c> tras sus transformaciones),
    /// como cota superior para cualquier tRNS o expansión de paleta posterior a IHDR.
    /// </summary>
    public int BytesPorPixelNativos => MuestrasPorPixelEnElPeorCaso * (ProfundidadBits == 16 ? 2 : 1);

    /// <summary>Memoria nativa del búfer de todas las filas de un PNG entrelazado; 0 si no está entrelazado.</summary>
    public long BytesDeBufferEntrelazado => Entrelazado ? (long)Ancho * Alto * BytesPorPixelNativos : 0;

    private int MuestrasPorPixelEnElPeorCaso => TipoDeColor switch
    {
        0 or 4 => 2, // Gris (con tRNS pasa a gris con alfa) y gris con alfa.
        _ => 4,      // RGB (con tRNS pasa a RGBA), paleta (expandida a RGBA) y RGBA.
    };

    /// <summary>Null si no es un IHDR legible y dentro de la especificación: no es un PNG que se pueda presupuestar.</summary>
    public static CabeceraPng? Leer(ReadOnlySpan<byte> c)
    {
        if (c.Length < LongitudMinima
            || !c[..8].SequenceEqual(FirmaPng)
            || BinaryPrimitives.ReadUInt32BigEndian(c.Slice(8, 4)) != 13
            || !c.Slice(12, 4).SequenceEqual("IHDR"u8))
            return null;

        var ancho = BinaryPrimitives.ReadUInt32BigEndian(c.Slice(16, 4));
        var alto = BinaryPrimitives.ReadUInt32BigEndian(c.Slice(20, 4));
        if (ancho == 0 || alto == 0 || ancho > int.MaxValue || alto > int.MaxValue)
            return null;

        int profundidad = c[24], tipoDeColor = c[25], compresion = c[26], filtro = c[27], entrelazado = c[28];

        // La especificación solo define el método 0 de compresión (deflate) y el 0 de filtro (adaptativo).
        if (compresion != 0 || filtro != 0)
            return null;

        // Combinaciones de la especificación del PNG (tabla de tipos de color y profundidades).
        var profundidadValida = tipoDeColor switch
        {
            0 => profundidad is 1 or 2 or 4 or 8 or 16,
            3 => profundidad is 1 or 2 or 4 or 8,
            2 or 4 or 6 => profundidad is 8 or 16,
            _ => false,
        };

        // 0 sin entrelazar, 1 Adam7: cualquier otro valor no es un PNG.
        if (!profundidadValida || entrelazado is not (0 or 1))
            return null;

        return new CabeceraPng((int)ancho, (int)alto, profundidad, tipoDeColor, entrelazado == 1);
    }
}
