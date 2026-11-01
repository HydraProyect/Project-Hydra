using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using CaeManager.Application.Common;

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
    /// memoria; los topes de filas y celdas limitan también ese coste.
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
    /// <exception cref="ArchivoRechazadoException">
    /// No es un .zip legible o rebasa algún tope (<see cref="LimiteHojaImportacionException"/> si es de filas o de
    /// celdas). Los mensajes no incluyen el nombre del archivo ni nada de su contenido. La pantalla de importación la
    /// captura y muestra «archivo no válido» o el motivo del tope; no es un error del sistema.
    /// </exception>
    public static void Verificar(Stream archivo, OpcionesLimitesImportacion? opciones = null)
    {
        ArgumentNullException.ThrowIfNull(archivo);
        opciones ??= new OpcionesLimitesImportacion();
        opciones.Validar();
        if (!archivo.CanSeek)
            throw new ArgumentException("El flujo del archivo debe admitir búsqueda.", nameof(archivo));

        var posicionInicial = archivo.Position;
        try
        {
            using var paquete = new ZipArchive(archivo, ZipArchiveMode.Read, leaveOpen: true);

            if (paquete.Entries.Count > MaximoEntradas)
                throw new ArchivoRechazadoException("El archivo contiene demasiadas partes.");

            long total = 0;
            var bufer = ArrayPool<byte>.Shared.Rent(TamanoBufer);
            try
            {
                foreach (var entrada in paquete.Entries)
                {
                    // Rechazo barato por cabecera. Falsificable, y por eso solo
                    // adelanta lo que el recuento real confirmaría después.
                    if (entrada.Length > MaximoBytesDescomprimidos)
                        throw new ArchivoRechazadoException("El archivo supera el tamaño descomprimido admitido.");

                    var comprimido = Math.Max(entrada.CompressedLength, 1);
                    long deEstaEntrada = 0;

                    using var flujoEntrada = entrada.Open();
                    int leidos;
                    while ((leidos = flujoEntrada.Read(bufer, 0, bufer.Length)) > 0)
                    {
                        deEstaEntrada += leidos;
                        total += leidos;

                        if (total > MaximoBytesDescomprimidos)
                            throw new ArchivoRechazadoException("El archivo supera el tamaño descomprimido admitido.");

                        if (deEstaEntrada > UmbralRatioBytes && deEstaEntrada / comprimido > RatioMaximo)
                            throw new ArchivoRechazadoException("El archivo tiene una tasa de compresión anómala.");
                    }
                }
                // Topes de filas y celdas. El tipo real de una parte se reconoce por su elemento raíz, no por el nombre que
                // le pone quien fabrica el paquete (la ruta, la extensión y el tipo de contenido declarado son suyos): se
                // intenta leer la raíz de TODA parte, y el lector XML decide la codificación. Una parte que no es XML
                // legible no puede ser una hoja para ClosedXML y se ignora. Sin depender de [Content_Types].xml, para que
                // su ausencia o su capitalización no desactive el control.
                var celdasDelLibro = 0L;
                foreach (var entrada in paquete.Entries)
                    celdasDelLibro += VerificarHoja(entrada, opciones, celdasDelLibro);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bufer, clearArray: true);
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException)
        {
            // Zip ilegible o XML mal formado: el mensaje original puede citar fragmentos del archivo, así que no se
            // propaga; la causa queda en InnerException solo para quien depure con el archivo delante.
            throw new ArchivoRechazadoException("El archivo no es un paquete .xlsx legible.", ex);
        }
        finally
        {
            // Si algo lanzó, el llamador no sigue; si todo fue bien, ClosedXML
            // necesita el flujo al principio del paquete.
            if (archivo.CanSeek)
                archivo.Position = posicionInicial;
        }
    }

    /// <summary>
    /// Cuenta filas y celdas de la parte si es una hoja de cálculo, y rechaza al rebasar el tope de filas, el de celdas
    /// de la hoja o el de celdas del libro (<paramref name="celdasPrevias"/> son las de las hojas ya examinadas).
    /// Devuelve las celdas de esta parte (0 si no es una hoja).
    /// </summary>
    private static long VerificarHoja(ZipArchiveEntry entrada, OpcionesLimitesImportacion opciones, long celdasPrevias)
    {
        using var flujo = entrada.Open();
        using var lector = XmlReader.Create(flujo, new XmlReaderSettings
        {
            // Ignore y no Prohibit: una hoja con DOCTYPE que el lector de ClosedXML aceptase no debe escapar a los topes; con
            // Ignore el DTD se salta sin resolver ni expandir entidades.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = MaximoBytesDescomprimidos
        });
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string nsStrict = "http://purl.oclc.org/ooxml/spreadsheetml/main";
        try
        {
            lector.MoveToContent();
        }
        catch (XmlException)
        {
            return 0; // no es XML legible (binario, vacío, codificación incoherente): tampoco la leería ClosedXML como hoja
        }

        if (lector.NamespaceURI is not (ns or nsStrict)) return 0;
        if (lector.LocalName == "sst")
        {
            // Tabla de cadenas compartidas: ClosedXML la carga entera aunque ninguna celda la use.
            long cadenas = 0;
            while (lector.Read())
                if (lector.NodeType == XmlNodeType.Element && lector.LocalName == "si" && ++cadenas > opciones.MaximoCeldasPorLibro)
                    throw new LimiteHojaImportacionException(false);
            return 0;
        }

        if (lector.LocalName != "worksheet") return 0;

        var filas = 0;
        long celdas = 0;
        while (lector.Read())
        {
            if (lector.NodeType != XmlNodeType.Element || lector.NamespaceURI is not (ns or nsStrict)) continue;
            if (lector.LocalName == "row")
            {
                // Una fila con número enorme pero pocas celdas también cuesta (ClosedXML la materializa): se mira el
                // número declarado y no solo el recuento. Una referencia ilegible o fuera de rango se rechaza.
                if (++filas > opciones.MaximoFilasPorHoja
                    || (lector.GetAttribute("r") is { } numero && NumeroDeFila(numero) > opciones.MaximoFilasPorHoja))
                    throw new LimiteHojaImportacionException(true);
            }
            else if (lector.LocalName == "c")
            {
                if (++celdas > opciones.MaximoCeldasPorHoja || celdasPrevias + celdas > opciones.MaximoCeldasPorLibro)
                    throw new LimiteHojaImportacionException(false);
                if (lector.GetAttribute("r") is { } referencia)
                {
                    var inicioFila = 0;
                    while (inicioFila < referencia.Length && char.IsAsciiLetter(referencia[inicioFila])) inicioFila++;
                    if (NumeroDeFila(referencia.AsSpan(inicioFila)) > opciones.MaximoFilasPorHoja)
                        throw new LimiteHojaImportacionException(true);
                }
            }
        }
        return celdas;
    }

    private static long NumeroDeFila(ReadOnlySpan<char> texto) =>
        uint.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var numero)
            ? numero
            : throw new ArchivoRechazadoException("El archivo tiene una referencia de fila no válida.");
}