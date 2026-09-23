namespace CaeManager.Application.Common;

/// <summary>
/// Tope único de tamaño por archivo que sube una persona desde la interfaz:
/// documentos (alta individual y subida múltiple, también cada entrada de un
/// .zip), plantillas, documentación de Centro y evidencias de Blindaje 42 y de
/// verificación externa de Subcontrata. Lo usan tanto Web
/// (<c>IBrowserFile.OpenReadStream(maxAllowedSize)</c> y la comprobación previa
/// del tamaño declarado) como los validadores de Application, para que la
/// pantalla y el comando no puedan divergir.
///
/// <para>
/// Ni Kestrel ni SignalR ponen otro tope a estas subidas: <c>InputFile</c>
/// trae el archivo por interop JS en trozos, así que el techo efectivo es el
/// <c>maxAllowedSize</c> que se le pasa, es decir, esta constante.
/// </para>
///
/// <para>
/// No cubre límites de otra naturaleza: imágenes de firma y sello (5 MB, en sus
/// comandos), adjuntos de correo (<see cref="Integraciones.LimitesAdjuntosCorreo"/>)
/// ni medios de WhatsApp. Los textos de interfaz que todavía dicen «10 MB» a mano
/// se alinean con esta constante al migrar cada pantalla a recursos (como ya hace
/// la Subida múltiple).
/// </para>
/// </summary>
public static class LimitesArchivoSubido
{
    /// <summary>
    /// <c>int</c> y no <c>long</c>: los validadores lo comparan con
    /// <c>byte[].Length</c> en un patrón de propiedad, que exige una constante
    /// del mismo tipo. En Web se convierte a <c>long</c> sin pérdida.
    /// </summary>
    public const int TamanoMaximoBytes = 10 * 1024 * 1024;

    /// <summary>El mismo tope en MB enteros, para los textos que lo nombran.</summary>
    public const int TamanoMaximoMb = TamanoMaximoBytes / (1024 * 1024);
}
