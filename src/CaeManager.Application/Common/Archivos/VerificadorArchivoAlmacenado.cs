using System.Security.Cryptography;

namespace CaeManager.Application.Common.Archivos;

/// <summary>
/// Puerto de solo lectura para comprobar el contenido de un archivo ya almacenado antes de que un Documento lo
/// referencie. Existe separado de <see cref="IFileStorageService"/> a propósito: <c>RenovarDocumentoCommandHandler</c>
/// no puede recibir el servicio de almacenamiento (renovar no borra, ver <c>RenovarDocumentoNoBorraArchivosTests</c>),
/// y esta comprobación solo necesita leer.
/// </summary>
public interface IVerificadorArchivoAlmacenado
{
    /// <summary>
    /// Verdadero si la clave existe en el almacenamiento del Tenant activo, su extensión es la de un Documento admitido
    /// (PDF, JPG, PNG o Word .docx) y la firma de su contenido real coincide con esa extensión
    /// (<see cref="ValidadorFirmaArchivo.TieneFormatoDeDocumento"/>). Una clave inexistente, de otro Tenant o cuyo
    /// contenido no supera la comprobación de integridad del almacenamiento es «no válido», nunca una excepción.
    /// </summary>
    Task<bool> EsDocumentoValidoAsync(string archivoUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// OWASP ASVS 4.0.3 V12.1/V12.2: el tipo de un archivo subido se decide por su contenido, no por el nombre. Hasta que
/// haya antivirus, todo archivo que entre como Documento se valida por firma; la clave del almacenamiento conserva la
/// extensión con la que se guardó, así que sirve de nombre para comparar.
///
/// <para>
/// Solo lee la cabecera. El tope de tamaño por archivo ya se aplicó al subirlo (cada archivo, antes de convertirlo), y el
/// PDF que resulta de unificar varios puede pasar de ese tope sin ser un problema: volver a aplicarlo aquí rechazaría
/// como «no válido» un Documento legítimo que el usuario acaba de componer.
/// </para>
/// </summary>
public sealed class VerificadorArchivoAlmacenado(IFileStorageService almacenamiento) : IVerificadorArchivoAlmacenado
{
    public async Task<bool> EsDocumentoValidoAsync(string archivoUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archivoUrl))
            return false;

        Stream flujo;
        try
        {
            flujo = await almacenamiento.AbrirAsync(archivoUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException
            or InvalidDataException or CryptographicException)
        {
            return false;
        }

        await using (flujo)
        {
            var cabecera = new byte[ValidadorFirmaArchivo.BytesDeCabeceraNecesarios];
            var leidos = await flujo.ReadAtLeastAsync(cabecera, cabecera.Length, throwOnEndOfStream: false, cancellationToken);

            return ValidadorFirmaArchivo.TieneFormatoDeDocumento(cabecera.AsSpan(0, leidos).ToArray(), archivoUrl);
        }
    }
}
