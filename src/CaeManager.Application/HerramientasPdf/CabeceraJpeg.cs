using System.Buffers.Binary;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Lo que la cabecera de un JPEG dice de la memoria que necesitará el decodificador, leído SIN decodificar. Un JPEG
/// progresivo, o uno secuencial cuyo primer barrido no lleva todos los componentes (varios barridos), obliga a libjpeg
/// (el que usa Skia) a mantener en memoria nativa los coeficientes DCT de TODA la imagen antes de poder dibujar la primera
/// línea: <see cref="BytesDeBufferDeCoeficientes"/> es esa cantidad, que no es el bitmap decodificado ni se ve en las
/// dimensiones por sí sola (depende de los componentes y del muestreo).
///
/// <para>
/// <b>Cálculo.</b> Es el de <c>jinit_d_coef_controller</c> de libjpeg: por cada componente, bloques de 8 x 8 con 64
/// coeficientes de 2 bytes (<c>JCOEF</c>), con el ancho en bloques igual a ceil(ancho x H / (Hmax x 8)), el alto
/// análogo, y ambos redondeados hacia arriba al múltiplo del muestreo del componente (la MCU). Un JPEG de 4:4:4 cuesta
/// 6 bytes por píxel; uno de 4:2:0, 3.
/// </para>
///
/// <para>
/// <b>Qué decide «varios barridos».</b> libjpeg lo declara por el PRIMER marcador SOS (<c>jdinput.c</c>): progresivo
/// (SOF2, SOF6, SOF10, SOF14) o primer barrido con menos componentes que el fotograma. Se leen solo las cabeceras hasta
/// ese SOS, no los datos comprimidos. Si no se llega al SOS (fichero truncado o cabecera rara) se cuenta como varios
/// barridos: sobreestimar es la dirección segura.
/// </para>
/// </summary>
public sealed record CabeceraJpeg(int Ancho, int Alto, bool Progresivo, bool VariosBarridos, IReadOnlyList<(int H, int V)> Muestreo)
{
    /// <summary>Máximo de componentes que acepta libjpeg (<c>MAX_COMPONENTS</c>).</summary>
    private const int MaximoComponentes = 10;

    private const long BytesPorBloque = 64 * 2;

    /// <summary>Si el decodificador tiene que guardar toda la imagen en coeficientes antes de producir píxeles.</summary>
    public bool NecesitaBufferDeCoeficientes => Progresivo || VariosBarridos;

    /// <summary>Memoria nativa del búfer de coeficientes; 0 si el JPEG se decodifica de una sola pasada.</summary>
    public long BytesDeBufferDeCoeficientes
    {
        get
        {
            if (!NecesitaBufferDeCoeficientes)
                return 0;

            var hMax = Muestreo.Max(m => m.H);
            var vMax = Muestreo.Max(m => m.V);
            long total = 0;
            foreach (var (h, v) in Muestreo)
            {
                var ancho = RedondearAlMultiplo(DividirHaciaArriba((long)Ancho * h, 8L * hMax), h);
                var alto = RedondearAlMultiplo(DividirHaciaArriba((long)Alto * v, 8L * vMax), v);
                total += ancho * alto * BytesPorBloque;
            }

            return total;
        }
    }

    /// <summary>Null si no hay un SOF legible: no es un JPEG que se pueda presupuestar.</summary>
    public static CabeceraJpeg? Leer(ReadOnlySpan<byte> c)
    {
        if (c.Length < 4 || c[0] != 0xFF || c[1] != 0xD8)
            return null;

        CabeceraJpeg? marco = null;
        var i = 2;
        while (i + 4 <= c.Length)
        {
            if (c[i] != 0xFF)
                break;

            var marcador = c[i + 1];
            if (marcador == 0xFF)
            {
                i++;
                continue;
            }

            if (marcador is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                i += 2;
                continue;
            }

            // EOI o relleno antes del primer barrido: las cabeceras terminaron sin SOS.
            if (marcador is 0x00 or 0xD9)
                break;

            var longitud = BinaryPrimitives.ReadUInt16BigEndian(c.Slice(i + 2, 2));
            if (longitud < 2 || i + 2 + longitud > c.Length)
                break;

            if (marcador == 0xDA)
            {
                // Primer barrido: Ns (componentes del barrido) está justo después de la longitud.
                if (marco is null)
                    return null;
                if (longitud < 3)
                    break;
                var enElBarrido = c[i + 4];
                return marco with { VariosBarridos = enElBarrido < marco.Muestreo.Count };
            }

            // Solo cuenta el primer SOF: libjpeg rechaza un segundo.
            if (marco is null && marcador is >= 0xC0 and <= 0xCF && marcador is not (0xC4 or 0xC8 or 0xCC))
            {
                marco = LeerSof(c.Slice(i, 2 + longitud), marcador);
                if (marco is null)
                    return null;
            }

            i += 2 + longitud;
        }

        // Sin SOS alcanzable: se presupuesta como la peor lectura (varios barridos).
        return marco is null ? null : marco with { VariosBarridos = true };
    }

    private static CabeceraJpeg? LeerSof(ReadOnlySpan<byte> segmento, byte marcador)
    {
        // marcador (2) + longitud (2) + P (1) + alto (2) + ancho (2) + Nf (1) + 3 bytes por componente.
        if (segmento.Length < 10)
            return null;

        var componentes = segmento[9];
        if (componentes is < 1 or > MaximoComponentes || segmento.Length != 10 + 3 * componentes)
            return null;

        var alto = BinaryPrimitives.ReadUInt16BigEndian(segmento.Slice(5, 2));
        var ancho = BinaryPrimitives.ReadUInt16BigEndian(segmento.Slice(7, 2));
        if (ancho == 0 || alto == 0)
            return null;

        var muestreo = new List<(int H, int V)>(componentes);
        for (var k = 0; k < componentes; k++)
        {
            var hv = segmento[11 + 3 * k];
            int h = hv >> 4, v = hv & 0x0F;
            if (h is < 1 or > 4 || v is < 1 or > 4)
                return null;
            muestreo.Add((h, v));
        }

        var progresivo = marcador is 0xC2 or 0xC6 or 0xCA or 0xCE;
        return new CabeceraJpeg(ancho, alto, progresivo, false, muestreo);
    }

    private static long DividirHaciaArriba(long numerador, long divisor) => (numerador + divisor - 1) / divisor;

    private static long RedondearAlMultiplo(long valor, int multiplo) => (valor + multiplo - 1) / multiplo * multiplo;
}
