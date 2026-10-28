using System.Buffers;
using System.IO.Compression;

namespace CaeManager.Infrastructure.Importacion;

/// <summary>
/// Puerta de entrada de todo .xlsx subido, antes de pasárselo a ClosedXML.
///
/// Un .xlsx es un .zip, y el tope de la pantalla de importación (5 / 20 MB)
/// acota el archivo comprimido, no lo que sale de él: un paquete diminuto
/// puede declarar gigabytes de XML (bomba de descompresión) y ClosedXML lo
/// cargaría entero en memoria dentro del proceso web único que atiende a todos
/// los Tenants, tumbando los circuitos de todos ellos. Esta inspección abre el
/// paquete con System.IO.Compression, lo lee en streaming descartando los
/// bytes y aborta en cuanto se rebasa alguno de los topes. Misma idea que
/// <c>ExtractorZip</c> (Web/Documentos), pero sin guardar nada.
///
/// Los topes se aplican sobre los bytes realmente descomprimidos, contados
/// mientras se leen: los tamaños que declara la cabecera del .zip los elige
/// quien fabrica el archivo y solo sirven como rechazo barato.
/// </summary>
public static class InspectorXlsxSeguro
{
    /// <summary>
    /// Un libro de Excel real tiene unas decenas de partes (hojas, estilos,
    /// cadenas compartidas, temas, relaciones); con cien hojas no pasa de
    /// ~120. Mil deja un margen holgado y frena los paquetes con decenas de
    /// miles de entradas. El directorio central ya se ha leído al comprobarlo,
    /// pero con un paquete de 20 MB como mucho ese coste queda acotado.
    /// </summary>
    public const int MaximoEntradas = 1_000;

    /// <summary>
    /// Tope del XML descomprimido sumando todas las partes. 100 MiB está muy
    /// por encima de cualquier plantilla de importación de la Plataforma
    /// (decenas de miles de filas como mucho) y frena la expansión
    /// desproporcionada, que es el vector de la bomba. No mide el coste en
    /// memoria del DOM de ClosedXML, que pesa varias veces el XML de origen:
    /// un libro denso y poco repetitivo dentro del tope sigue costando
    /// memoria, y acotarlo (filas, celdas o análisis concurrentes) es un
    /// control aparte.
    /// </summary>
    public const long MaximoBytesDescomprimidos = 100L * 1024 * 1024;

    /// <summary>
    /// Ratio descomprimido/comprimido máximo por entrada. El XML de una hoja
    /// real comprime entre 5:1 y 30:1; un relleno repetitivo (el vector de las
    /// bombas) supera 500:1. Con 100:1 no hay falsos positivos en libros
    /// reales y se corta la bomba mucho antes de llegar al tope absoluto.
    /// </summary>
    public const int RatioMaximo = 100;

    /// <summary>
    /// Por debajo de este tamaño descomprimido el ratio no se evalúa: partes
    /// minúsculas (un rels de 200 bytes comprimido a 40) dan ratios altos
    /// sin ningún riesgo.
    /// </summary>
    public const long UmbralRatioBytes = 1024 * 1024;

    private const int TamanoBufer = 64 * 1024;

    /// <summary>
    /// Verifica que el paquete se puede abrir con seguridad y deja el flujo en
    /// la posición en que estaba, listo para <c>new XLWorkbook(archivo)</c>.
    /// </summary>
    /// <param name="archivo">Flujo con búsqueda (los llamadores pasan un <see cref="MemoryStream"/>).</param>
    /// <exception cref="InvalidDataException">
    /// No es un .zip legible o rebasa algún tope. Los mensajes no incluyen el
    /// nombre del archivo ni nada de su contenido. La pantalla de importación
    /// captura cualquier excepción del análisis y muestra «archivo no válido».
    /// </exception>
    public static void Verificar(Stream archivo)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        if (!archivo.CanSeek)
            throw new ArgumentException("El flujo del archivo debe admitir búsqueda.", nameof(archivo));

        var posicionInicial = archivo.Position;
        try
        {
            using var paquete = new ZipArchive(archivo, ZipArchiveMode.Read, leaveOpen: true);

            if (paquete.Entries.Count > MaximoEntradas)
                throw new InvalidDataException("El archivo contiene demasiadas partes.");

            long total = 0;
            var bufer = ArrayPool<byte>.Shared.Rent(TamanoBufer);
            try
            {
                foreach (var entrada in paquete.Entries)
                {
                    // Rechazo barato por cabecera. Falsificable, y por eso solo
                    // adelanta lo que el recuento real confirmaría después.
                    if (entrada.Length > MaximoBytesDescomprimidos)
                        throw new InvalidDataException("El archivo supera el tamaño descomprimido admitido.");

                    var comprimido = Math.Max(entrada.CompressedLength, 1);
                    long deEstaEntrada = 0;

                    using var flujoEntrada = entrada.Open();
                    int leidos;
                    while ((leidos = flujoEntrada.Read(bufer, 0, bufer.Length)) > 0)
                    {
                        deEstaEntrada += leidos;
                        total += leidos;

                        if (total > MaximoBytesDescomprimidos)
                            throw new InvalidDataException("El archivo supera el tamaño descomprimido admitido.");

                        if (deEstaEntrada > UmbralRatioBytes && deEstaEntrada / comprimido > RatioMaximo)
                            throw new InvalidDataException("El archivo tiene una tasa de compresión anómala.");
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bufer, clearArray: true);
            }
        }
        finally
        {
            // Si algo lanzó, el llamador no sigue; si todo fue bien, ClosedXML
            // necesita el flujo al principio del paquete.
            if (archivo.CanSeek)
                archivo.Position = posicionInicial;
        }
    }
}
