using System.Buffers.Binary;
using CaeManager.Domain.Common;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Topes de Foto a PDF. Las fotos las sube un tercero y se decodifican en el proceso web, que sirve a todos los Tenants:
/// cada tope acota un coste distinto (bytes que se retienen, número de decodificaciones y memoria de cada una).
/// </summary>
public static class LimitesFotoAPdf
{
    /// <summary>Fotos por PDF.</summary>
    public const int MaximoFotos = 20;

    /// <summary>Una foto de móvil a máxima resolución en JPEG ronda los 10 MB; con 15 MB caben sin dejar pasar un fichero de cualquier tamaño.</summary>
    public const long TamanoMaximoFotoBytes = 15L * 1024 * 1024;

    public const int TamanoMaximoFotoMb = (int)(TamanoMaximoFotoBytes / (1024 * 1024));

    /// <summary>Suma de los ficheros de una misma sesión de trabajo: acota lo que se retiene en disco y lo que se lee al generar.</summary>
    public const long TamanoMaximoTotalBytes = 80L * 1024 * 1024;

    public const int TamanoMaximoTotalMb = (int)(TamanoMaximoTotalBytes / (1024 * 1024));

    /// <summary>
    /// Píxeles declarados (ancho x alto) que se admiten, leídos de la cabecera ANTES de decodificar: el coste de abrir una
    /// imagen lo fijan sus dimensiones, no su tamaño en disco (un PNG de unos KB puede declarar miles de millones de píxeles).
    /// 40 megapíxeles dejan pasar una foto de 7000 x 5600; una cámara de 50 MP o más se reduce antes de subirla.
    /// </summary>
    public const long MaximoPixeles = 40_000_000;

    /// <summary>Tope por lado: una tira de 1 x 40 millones de píxeles cabe en <see cref="MaximoPixeles"/> y no es una foto.</summary>
    public const int MaximoLadoPixeles = 12_000;

    /// <summary>Memoria de un píxel decodificado o dibujado (RGBA de 8 bits por canal).</summary>
    public const long BytesPorPixelDecodificado = 4;

    /// <summary>
    /// Copias del JPEG recodificado que retiene el documento PDF en memoria mientras se compone (el flujo que lee la
    /// imagen y el objeto de imagen del documento), medidas con PDFsharp; la cuenta se redondea hacia arriba. Se suman a
    /// la copia que ya es el PDF final. El tamaño del JPEG NO se deduce del de la foto subida (un PNG de 170 KB con un
    /// patrón repetido da un JPEG de varios MB): se cuenta lo que el codificador produce de verdad.
    /// </summary>
    public const long CopiasDelJpegEnElDocumento = 2;

    /// <summary>
    /// Bytes máximos de la miniatura JPEG que la pantalla retiene en el circuito mientras la foto está en la lista. Se
    /// guarda como bytes (no como Base64, que en memoria ocupa más del doble) y se reserva en el presupuesto de memoria
    /// con su tamaño real hasta quitar la foto o desechar la pantalla; una miniatura mayor no se retiene (la foto entra
    /// igual, sin vista previa). Una de 128 px a calidad 90 ocupa de 5 a 15 KB, y 32 KB cubre incluso una foto de ruido;
    /// con 20 fotos son 640 KB por circuito como máximo.
    /// </summary>
    public const long TamanoMaximoMiniaturaBytes = 32 * 1024;

    /// <summary>Lado mayor de la imagen que entra en el PDF (A4 a 300 ppp); una foto más grande se reduce y una menor no se amplía.</summary>
    public const int LadoMaximoEnPdfPixeles = 3508;
}

/// <summary>Formatos que se aceptan, decididos por la firma de los bytes y nunca por la extensión ni por lo que declare el navegador.</summary>
public enum FormatoFoto
{
    Jpeg,
    Png,
}

public readonly record struct FotoValidada(FormatoFoto Formato, int Ancho, int Alto)
{
    public long Pixeles => (long)Ancho * Alto;
}

/// <summary>Códigos de fallo de Foto a PDF; la pantalla traduce cada uno.</summary>
public static class ErroresFotoAPdf
{
    public const string NoDisponible = "FotoAPdf.NoDisponible";
    public const string SinFotos = "FotoAPdf.SinFotos";
    public const string DemasiadasFotos = "FotoAPdf.DemasiadasFotos";
    public const string FotoVacia = "FotoAPdf.FotoVacia";
    public const string FotoDemasiadoGrande = "FotoAPdf.FotoDemasiadoGrande";
    public const string TotalDemasiadoGrande = "FotoAPdf.TotalDemasiadoGrande";
    public const string FormatoNoSoportado = "FotoAPdf.FormatoNoSoportado";
    public const string HeicNoSoportado = "FotoAPdf.HeicNoSoportado";
    public const string FotoIlegible = "FotoAPdf.FotoIlegible";
    public const string DemasiadosPixeles = "FotoAPdf.DemasiadosPixeles";
    public const string NoSePudoGenerar = "FotoAPdf.NoSePudoGenerar";
}

/// <summary>
/// Comprueba una foto subida sin decodificarla: tamaño, firma de bytes y dimensiones declaradas por la cabecera. Solo
/// cuentan los bytes; el nombre y el tipo MIME que declare el navegador no se miran. Lo usa la pantalla al añadir cada
/// foto y lo repite el generador al generar (una foto no se da por buena por haberlo sido antes), y el procesador de
/// Infrastructure lo vuelve a comprobar con las dimensiones que lee el propio decodificador.
/// </summary>
public static class ValidadorFotoAPdf
{
    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Marcas (brand) de la caja <c>ftyp</c> de un contenedor HEIF/HEIC.</summary>
    private static readonly string[] MarcasHeif = ["heic", "heix", "hevc", "hevx", "heim", "heis", "hevm", "hevs", "mif1", "msf1", "heif"];

    public static Result<FotoValidada> Validar(byte[] contenido)
    {
        if (contenido.Length == 0)
            return Fallo(ErroresFotoAPdf.FotoVacia, "La foto está vacía.");

        if (contenido.Length > LimitesFotoAPdf.TamanoMaximoFotoBytes)
            return Fallo(ErroresFotoAPdf.FotoDemasiadoGrande, "La foto supera el tamaño máximo.");

        FormatoFoto formato;
        (long Ancho, long Alto)? dimensiones;
        if (contenido.AsSpan().StartsWith(FirmaPng))
        {
            formato = FormatoFoto.Png;
            dimensiones = DimensionesDePng(contenido);
        }
        else if (contenido.Length >= 3 && contenido[0] == 0xFF && contenido[1] == 0xD8 && contenido[2] == 0xFF)
        {
            formato = FormatoFoto.Jpeg;
            dimensiones = DimensionesDeJpeg(contenido);
        }
        else
        {
            return EsHeif(contenido)
                ? Fallo(ErroresFotoAPdf.HeicNoSoportado, "Las fotos HEIC no se admiten.")
                : Fallo(ErroresFotoAPdf.FormatoNoSoportado, "Solo se admiten fotos JPEG y PNG.");
        }

        if (dimensiones is not { } declaradas || declaradas.Ancho <= 0 || declaradas.Alto <= 0)
            return Fallo(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");

        var (ancho, alto) = declaradas;

        // Cada lado primero: con los dos como uint (PNG) el producto no cabe en long y daba negativo.
        if (ancho > LimitesFotoAPdf.MaximoLadoPixeles || alto > LimitesFotoAPdf.MaximoLadoPixeles
            || ancho * alto > LimitesFotoAPdf.MaximoPixeles)
            return Fallo(ErroresFotoAPdf.DemasiadosPixeles, "La foto declara más píxeles de los admitidos.");

        return Result.Exito(new FotoValidada(formato, (int)ancho, (int)alto));
    }

    private static bool EsHeif(byte[] c)
    {
        if (c.Length < 12 || !c.AsSpan(4, 4).SequenceEqual("ftyp"u8))
            return false;

        var marca = System.Text.Encoding.ASCII.GetString(c, 8, 4);
        return MarcasHeif.Contains(marca, StringComparer.Ordinal);
    }

    /// <summary>El primer trozo de un PNG es siempre IHDR (13 bytes), y lleva ancho y alto en los 8 primeros.</summary>
    private static (long Ancho, long Alto)? DimensionesDePng(byte[] c)
    {
        if (c.Length < 33
            || BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(8, 4)) != 13
            || !c.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            return null;

        return (BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(20, 4)));
    }

    /// <summary>
    /// En un JPEG las dimensiones viven en el marcador SOF, detrás de un número indeterminado de segmentos (EXIF, ICC,
    /// miniaturas). Se recorren por su longitud declarada, de modo que lo que haya DENTRO de un segmento (una miniatura
    /// con su propio SOF) no se confunde con la imagen. Un SOF con alto 0 declara el alto más tarde (DNL): se rechaza.
    /// </summary>
    private static (long Ancho, long Alto)? DimensionesDeJpeg(byte[] c)
    {
        var i = 2;
        while (i + 4 <= c.Length)
        {
            if (c[i] != 0xFF)
                return null;

            var marcador = c[i + 1];
            if (marcador == 0xFF)
            {
                i++;
                continue;
            }

            // Sin carga útil: TEM, RSTn y SOI. Un EOI antes de cualquier SOF es un fichero roto.
            if (marcador is 0x01 or (>= 0xD0 and <= 0xD8))
            {
                i += 2;
                continue;
            }

            // 0x00 es relleno de datos comprimidos, EOI y SOS cierran las cabeceras: ninguno puede preceder a un SOF.
            if (marcador is 0x00 or 0xD9 or 0xDA)
                return null;

            var longitud = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(i + 2, 2));
            if (longitud < 2 || i + 2 + longitud > c.Length)
                return null;

            // SOF0-SOF15 salvo DHT (C4), JPG (C8) y DAC (CC), que caen en el mismo rango sin ser cabecera de fotograma.
            if (marcador is >= 0xC0 and <= 0xCF && marcador is not (0xC4 or 0xC8 or 0xCC))
            {
                if (longitud < 8)
                    return null;

                var alto = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(i + 5, 2));
                var ancho = BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(i + 7, 2));
                return (ancho, alto);
            }

            i += 2 + longitud;
        }

        return null;
    }

    private static Result<FotoValidada> Fallo(string codigo, string mensaje) =>
        Result.Fallo<FotoValidada>(Error.Crear(codigo, mensaje));
}

/// <summary>
/// Decodifica las fotos y arma el PDF, en este proceso: ni las fotos ni el PDF salen de TALVEG. Lo implementa
/// Infrastructure (SkiaSharp para decodificar y PDFsharp para componer); Application solo fija el contrato.
///
/// El PDF resultante no lleva metadatos de las fotos: ni EXIF (GPS, modelo de cámara, fecha) ni perfiles. La orientación
/// EXIF se aplica a los píxeles, de modo que la página sale derecha sin depender de que el visor lea nada.
/// </summary>
public interface IProcesadorFotosPdf
{
    /// <summary>
    /// Un PDF con una página por foto y en el orden recibido. Las fotos ya pasaron por <see cref="ValidadorFotoAPdf"/>.
    /// El procesador reserva en el presupuesto de memoria (<see cref="Common.IPresupuestoMemoriaDocumentos"/>) lo que
    /// decodifica, lo que codifica y lo que retiene, y sin esperar (quien llama ya retiene las fotos leídas): si no cabe,
    /// lanza <see cref="Common.PresupuestoMemoriaAgotadoException"/> antes de reservar de más. Cancelar interrumpe entre
    /// bandas de decodificación y entre etapas, y lanza <see cref="OperationCanceledException"/>.
    /// </summary>
    Result<PdfDeFotos> ComponerPdf(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken);

    /// <summary>Miniatura JPEG, ya orientada, con el lado mayor de <paramref name="ladoMaximoPixeles"/> como mucho. Mismas reglas de memoria y cancelación que <see cref="ComponerPdf"/>.</summary>
    Result<byte[]> CrearMiniatura(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken);
}

/// <summary>
/// El PDF ya compuesto, como flujo en posición 0 que se entrega tal cual (sin copiarlo a otro array), junto con la reserva
/// del presupuesto de memoria que lo cubre: quien lo recibe lo desecha cuando el navegador ya lo tiene y la reserva vuelve
/// al presupuesto con él.
/// </summary>
public sealed class PdfDeFotos(MemoryStream flujo, IDisposable reserva) : IDisposable
{
    public MemoryStream Flujo { get; } = flujo;

    public void Dispose()
    {
        Flujo.Dispose();
        reserva.Dispose();
    }
}
