using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Un archivo adjunto a un <see cref="Mensaje"/> — entrante (recibido
/// de verdad por Graph) o saliente (subido por un Gestor CAE al responder).
/// El contenido en sí vive en <c>IFileStorageService</c> (mismo servicio que
/// los PDFs de Documento, ver ARQUITECTURA-INTEGRACIONES.md § 12.3: "no un
/// storage paralelo") — <see cref="ArchivoUrl"/> es el identificador opaco
/// que devuelve ese servicio, nunca la ruta real ni una URL pública.
/// </summary>
public class AdjuntoMensaje : EntidadConTenant
{
    public const int LongitudMaximaNombreArchivo = 260;
    public const int LongitudMaximaTipoContenido = 150;
    public const int LongitudMaximaArchivoUrl = 500;

    public Guid MensajeId { get; private set; }
    public string NombreArchivo { get; private set; } = string.Empty;
    public string TipoContenido { get; private set; } = string.Empty;
    public long TamanoBytes { get; private set; }
    public string ArchivoUrl { get; private set; } = string.Empty;

    private AdjuntoMensaje()
    {
    }

    public AdjuntoMensaje(Guid mensajeId, string nombreArchivo, string tipoContenido, long tamanoBytes, string archivoUrl)
    {
        if (mensajeId == Guid.Empty)
            throw new ArgumentException("El adjunto debe pertenecer a un mensaje.", nameof(mensajeId));
        if (string.IsNullOrWhiteSpace(nombreArchivo))
            throw new ArgumentException("El adjunto debe tener un nombre de archivo.", nameof(nombreArchivo));
        if (nombreArchivo.Length > LongitudMaximaNombreArchivo)
            throw new ArgumentException($"El nombre del archivo no puede superar {LongitudMaximaNombreArchivo} caracteres.", nameof(nombreArchivo));
        if (string.IsNullOrWhiteSpace(tipoContenido))
            throw new ArgumentException("El adjunto debe tener un tipo de contenido.", nameof(tipoContenido));
        if (tipoContenido.Length > LongitudMaximaTipoContenido)
            throw new ArgumentException($"El tipo de contenido no puede superar {LongitudMaximaTipoContenido} caracteres.", nameof(tipoContenido));
        if (tamanoBytes < 0)
            throw new ArgumentException("El tamaño no puede ser negativo.", nameof(tamanoBytes));
        if (string.IsNullOrWhiteSpace(archivoUrl))
            throw new ArgumentException("El adjunto debe tener un archivo guardado.", nameof(archivoUrl));
        if (archivoUrl.Length > LongitudMaximaArchivoUrl)
            throw new ArgumentException($"La referencia de archivo no puede superar {LongitudMaximaArchivoUrl} caracteres.", nameof(archivoUrl));

        MensajeId = mensajeId;
        NombreArchivo = nombreArchivo.Trim();
        TipoContenido = tipoContenido.Trim();
        TamanoBytes = tamanoBytes;
        ArchivoUrl = archivoUrl;
    }
}
