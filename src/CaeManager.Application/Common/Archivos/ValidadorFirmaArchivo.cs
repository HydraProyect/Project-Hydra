using System.Resources;

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
    /// coincide con ella. Sin tope de tamaño: lo usa también la comprobación de un archivo ya almacenado, donde basta con
    /// la cabecera y el PDF unificado de una subida múltiple puede pasar legítimamente del tope por archivo.
    /// </summary>
    public static bool TieneFormatoDeDocumento(byte[] contenido, string? nombreArchivo) =>
        !string.IsNullOrWhiteSpace(nombreArchivo)
        && Path.GetExtension(nombreArchivo).ToLowerInvariant() is ".pdf" or ".jpg" or ".jpeg" or ".png" or ".docx"
        && TieneFirmaValida(contenido, nombreArchivo);

    /// <summary>Bytes iniciales que bastan para decidir la firma de cualquiera de los formatos admitidos (el mayor es PNG, 8).</summary>
    public const int BytesDeCabeceraNecesarios = 8;

    private static bool EmpiezaCon(byte[] contenido, byte[] firma) =>
        contenido.Length >= firma.Length && contenido.AsSpan(0, firma.Length).SequenceEqual(firma);
}
