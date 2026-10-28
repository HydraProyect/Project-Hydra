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
    /// (PDF, JPG, PNG o Word .docx), no pasa del tamaño máximo y su contenido real coincide con esa extensión
    /// (<see cref="ValidadorFirmaArchivo.EsDocumentoValido"/>). Una clave inexistente o de otro Tenant es «no válido».
    /// </summary>
    Task<bool> EsDocumentoValidoAsync(string archivoUrl, CancellationToken cancellationToken = default);
}

/// <summary>
/// OWASP ASVS 4.0.3 V12.1/V12.2: el tipo de un archivo subido se decide por su contenido, no por el nombre. Hasta que
/// haya antivirus, todo archivo que entre como Documento se valida por firma; la clave del almacenamiento conserva la
/// extensión con la que se guardó, así que sirve de nombre para comparar.
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
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or KeyNotFoundException)
        {
            return false;
        }

        await using (flujo)
        {
            using var memoria = new MemoryStream();
            var buffer = new byte[81920];
            int leidos;
            while ((leidos = await flujo.ReadAsync(buffer, cancellationToken)) > 0)
            {
                if (memoria.Length + leidos > LimitesArchivoSubido.TamanoMaximoBytes)
                    return false;
                await memoria.WriteAsync(buffer.AsMemory(0, leidos), cancellationToken);
            }

            return ValidadorFirmaArchivo.EsDocumentoValido(memoria.ToArray(), archivoUrl);
        }
    }
}
