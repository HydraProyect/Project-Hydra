using System.Buffers.Binary;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Lee el ancho y el alto declarados en la cabecera de un JPEG o un PNG
/// <b>sin decodificar la imagen</b>.
///
/// Hace falta porque el tamaño del archivo no acota en absoluto la memoria que
/// cuesta abrirlo: un PNG de un solo color se comprime muchísimo, así que las
/// dimensiones son libres. Medido sobre este código: un PNG de <b>136 KB</b>
/// —12000 x 12000, muy por debajo del tope de 10 MB de la subida— hacía que la
/// conversión reservara <b>789 MB</b>, un factor de 5800, y se aceptaba sin
/// oponer nada. Con el máximo de archivos por lote, eso basta para tumbar el
/// proceso web y con él los circuitos Blazor de todos los tenants de la
/// réplica.
///
/// Es un vector distinto del de la bomba .zip y no lo cubren los mismos
/// límites: el presupuesto del lote cuenta bytes de fichero, y aquí el daño no
/// está en los bytes que entran sino en los píxeles que se declaran.
///
/// Lo que se lee es la cabecera, es decir, lo que el archivo declara. A
/// diferencia del caso .zip —donde el decodificador de .NET impone lo
/// declarado— aquí lo declarado ES lo que se va a reservar: el decodificador
/// dimensiona el bitmap con esos valores. Así que comprobarlo antes de
/// decodificar es exactamente la defensa correcta, y no una aproximación.
/// </summary>
public static class DimensionesImagen
{
    /// <summary>
    /// Píxeles totales admitidos en una imagen. 50 megapíxeles deja pasar todo
    /// lo que aparece en la operativa real —una foto de 8000 x 6000 son 48 MP,
    /// un A4 escaneado a 600 ppp son 34,8— y rechaza lo que solo puede venir
    /// de un archivo construido para hacer daño.
    ///
    /// No es un límite de memoria: al ritmo medido (unos 5,5 MB reservados por
    /// megapíxel) una imagen en el límite todavía cuesta del orden de 275 MB.
    /// Acota el peor caso a algo del que el proceso se recupera, en vez de a
    /// varios GB; acotarlo de verdad exige decodificar fuera del proceso web,
    /// que es una decisión pendiente del informe del Módulo 2.
    /// </summary>
    public const long MaximoPixeles = 50_000_000;

    /// <summary>
    /// Píxeles declarados por la cabecera, o null si no se reconoce el formato
    /// (que es el caso de un PDF o un .docx: no son imágenes y no pasan por
    /// aquí).
    /// </summary>
    public static long? PixelesDeclarados(byte[] contenido) =>
        EsPng(contenido) ? PixelesDePng(contenido)
        : EsJpeg(contenido) ? PixelesDeJpeg(contenido)
        : null;

    /// <summary>
    /// Falso solo cuando la cabecera declara más píxeles de los admitidos. Un
    /// formato que no se reconoce NO se rechaza aquí: de la validación de
    /// contenido se encarga <see cref="ValidadorFirmaArchivo"/>, y hacer que
    /// esta comprobación opine sobre lo que no entiende la convertiría en un
    /// segundo validador de tipos, desacompasado del primero.
    /// </summary>
    public static bool EstaDentroDelLimite(byte[] contenido) =>
        PixelesDeclarados(contenido) is not { } pixeles || pixeles <= MaximoPixeles;

    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool EsPng(byte[] c) =>
        c.Length >= 24 && c.AsSpan(0, 8).SequenceEqual(FirmaPng);

    private static bool EsJpeg(byte[] c) =>
        c.Length >= 4 && c[0] == 0xFF && c[1] == 0xD8;

    /// <summary>El primer trozo de un PNG es siempre IHDR, y lleva ancho y alto en sus primeros 8 bytes.</summary>
    private static long PixelesDePng(byte[] contenido)
    {
        var ancho = BinaryPrimitives.ReadUInt32BigEndian(contenido.AsSpan(16, 4));
        var alto = BinaryPrimitives.ReadUInt32BigEndian(contenido.AsSpan(20, 4));
        return (long)ancho * alto;
    }

    /// <summary>
    /// En JPEG las dimensiones viven en el marcador SOF, que puede estar
    /// detrás de un número indeterminado de segmentos (EXIF, tablas de
    /// cuantización, miniaturas). Se recorren los segmentos por su longitud
    /// declarada hasta encontrarlo.
    /// </summary>
    private static long? PixelesDeJpeg(byte[] contenido)
    {
        var i = 2;

        while (i + 3 < contenido.Length)
        {
            if (contenido[i] != 0xFF) return null;

            var marcador = contenido[i + 1];

            // Marcadores de relleno y los que no llevan carga útil.
            if (marcador == 0xFF) { i++; continue; }
            if (marcador is 0x01 or >= 0xD0 and <= 0xD9) { i += 2; continue; }

            var longitud = BinaryPrimitives.ReadUInt16BigEndian(contenido.AsSpan(i + 2, 2));
            if (longitud < 2) return null;

            // SOF0-SOF3, SOF5-SOF7, SOF9-SOF11: los marcadores de inicio de
            // fotograma. Se excluyen DHT (0xC4), DAC (0xCC) y RSTn, que caen
            // en el mismo rango 0xC0-0xCF sin ser SOF.
            var esSof = marcador is >= 0xC0 and <= 0xCF && marcador is not (0xC4 or 0xC8 or 0xCC);
            if (esSof)
            {
                if (i + 9 > contenido.Length) return null;
                var alto = BinaryPrimitives.ReadUInt16BigEndian(contenido.AsSpan(i + 5, 2));
                var ancho = BinaryPrimitives.ReadUInt16BigEndian(contenido.AsSpan(i + 7, 2));
                return (long)ancho * alto;
            }

            // Inicio de los datos comprimidos: ya no hay más cabeceras que leer.
            if (marcador == 0xDA) return null;

            i += 2 + longitud;
        }

        return null;
    }
}
