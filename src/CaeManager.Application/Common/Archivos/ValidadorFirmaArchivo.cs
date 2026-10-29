using System.Resources;
using System.IO.Compression;

namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Comprueba que la cabecera real corresponde a la extensión declarada. Las
/// imágenes (.jpg/.jpeg/.png) aceptan firma JPEG o PNG indistintamente, como
/// el validador de subidas que existía antes de compartirse con Application.
/// </summary>
public static class ValidadorFirmaArchivo
{
    public static string MensajeDocumentoNoValido => new ResourceManager(
        "CaeManager.Application.Subcontratas.Recursos.ErroresVerificacionExterna",
        typeof(ValidadorFirmaArchivo).Assembly).GetString("ArchivoDocumentoNoValido")!;

    public static string MensajeRequisitoNoValido => new ResourceManager(
        "CaeManager.Application.Subcontratas.Recursos.ErroresVerificacionExterna",
        typeof(ValidadorFirmaArchivo).Assembly).GetString("ArchivoRequisitoNoValido")!;

    public static string MensajePlantillaNoValida => new ResourceManager(
        "CaeManager.Application.Subcontratas.Recursos.ErroresVerificacionExterna",
        typeof(ValidadorFirmaArchivo).Assembly).GetString("ArchivoPlantillaNoValido")!;

    private static readonly byte[] FirmaPdf = "%PDF-"u8.ToArray();
    private static readonly byte[] FirmaJpeg = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] FirmaPng = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // .docx es un ZIP (Office Open XML) — cabecera local de fichero ZIP.
    private static readonly byte[] FirmaZip = [0x50, 0x4B, 0x03, 0x04];

    public static bool TieneFirmaValida(byte[] contenido, string nombreArchivo) =>
        Path.GetExtension(nombreArchivo).ToLowerInvariant() switch
        {
            ".pdf" => EmpiezaCon(contenido, FirmaPdf),
            ".jpg" or ".jpeg" or ".png" => EmpiezaCon(contenido, FirmaJpeg) || EmpiezaCon(contenido, FirmaPng),
            ".docx" or ".zip" => EmpiezaCon(contenido, FirmaZip),
            _ => false
        };

    /// <summary>Documento que entra por una subida: tamaño dentro del tope por archivo y <see cref="TieneFormatoDeDocumento"/>.</summary>
    public static bool EsDocumentoValido(byte[] contenido, string? nombreArchivo) =>
        contenido.Length is > 0 and <= LimitesArchivoSubido.TamanoMaximoBytes
        && TieneFormatoDeDocumento(contenido, nombreArchivo);

    /// <summary>
    /// Extensión de documento admitida (PDF, JPG, JPEG, PNG o DOCX; nunca .zip suelto) y firma del contenido que
    /// coincide con ella. Sin tope de tamaño: lo usa también la comprobación de un archivo ya almacenado, donde el PDF
    /// unificado de una subida múltiple puede pasar legítimamente del tope por archivo. Para DOCX exige además el
    /// archivo entero y su estructura OOXML mínima (<see cref="EsOoxmlValido"/>), no solo la firma ZIP: con solo la
    /// cabecera de <see cref="BytesDeCabeceraNecesarios"/> bytes un DOCX nunca es válido.
    /// </summary>
    public static bool TieneFormatoDeDocumento(byte[] contenido, string? nombreArchivo) =>
        !string.IsNullOrWhiteSpace(nombreArchivo)
        && Path.GetExtension(nombreArchivo).ToLowerInvariant() is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".docx"
        && TieneFirmaValida(contenido, nombreArchivo)
        && (!Path.GetExtension(nombreArchivo).Equals(".docx", StringComparison.OrdinalIgnoreCase) || EsOoxmlValido(contenido));

    private const int MaximoEntradasOoxml = 2048;
    private const long MaximoBytesDescomprimidosOoxml = 100L * 1024 * 1024;

    /// <summary>
    /// Estructura OOXML mínima de un .docx: [Content_Types].xml y word/document.xml, una sola vez cada uno (los nombres
    /// de parte de OPC no distinguen mayúsculas ni admiten «\», así que «WORD/Document.xml» o una copia con otra
    /// capitalización cuentan como duplicado) y ambos con aspecto de XML. Sin descomprimir sin límite: tope de entradas y
    /// lectura real de todas las entradas con tope de bytes descomprimidos, porque el tamaño que declara la cabecera
    /// del ZIP lo elige quien fabrica el archivo. Una entrada cifrada o con datos corruptos no se puede leer y se rechaza.
    /// </summary>
    private static bool EsOoxmlValido(byte[] contenido)
    {
        try
        {
            using var flujo = new MemoryStream(contenido, writable: false);
            using var zip = new ZipArchive(flujo, ZipArchiveMode.Read);
            if (zip.Entries.Count > MaximoEntradasOoxml) return false;

            var nombres = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool tipos = false, documento = false;
            long total = 0;
            var bufer = new byte[64 * 1024];
            foreach (var entrada in zip.Entries)
            {
                var nombre = entrada.FullName.Replace('\\', '/');
                if (!nombres.Add(nombre)) return false;

                var esPartObligatoria = nombre.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)
                    || nombre.Equals("word/document.xml", StringComparison.OrdinalIgnoreCase);
                var primeros = true;
                using var lectura = entrada.Open();
                int leidos;
                while ((leidos = lectura.Read(bufer, 0, bufer.Length)) > 0)
                {
                    total += leidos;
                    if (total > MaximoBytesDescomprimidosOoxml) return false;
                    if (primeros && esPartObligatoria && !EmpiezaComoXml(bufer.AsSpan(0, leidos))) return false;
                    primeros = false;
                }

                if (esPartObligatoria && primeros) return false; // vacía: no puede ser XML
                if (nombre.Equals("[Content_Types].xml", StringComparison.OrdinalIgnoreCase)) tipos = true;
                else if (esPartObligatoria) documento = true;
            }
            return tipos && documento;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>¿Empieza como XML? Tras una marca de orden de bytes UTF-8 o UTF-16 y espacios en blanco, un «&lt;».</summary>
    private static bool EmpiezaComoXml(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            return true;
        var posicion = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        while (posicion < bytes.Length && bytes[posicion] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            posicion++;
        return posicion < bytes.Length && bytes[posicion] == (byte)'<';
    }
    /// <summary>Bytes iniciales que bastan para decidir la firma de cualquiera de los formatos admitidos (el mayor es PNG, 8).</summary>
    public const int BytesDeCabeceraNecesarios = 8;

    private static bool EmpiezaCon(byte[] contenido, byte[] firma) =>
        contenido.Length >= firma.Length && contenido.AsSpan(0, firma.Length).SequenceEqual(firma);
}
