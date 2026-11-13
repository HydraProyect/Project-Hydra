namespace CaeManager.Application.Common;

/// <summary>
/// Almacenamiento de archivos adjuntos (PDFs de Documento). La implementación
/// real vive en Infrastructure — hoy sobre disco local, preparada para
/// cambiar a almacenamiento en la nube sin tocar Application ni Presentation
/// (ver Project-Hydra-Negocio/tecnico/ARCHITECTURE.md, "Archivos").
/// </summary>
public interface IFileStorageService
{
    /// <summary>Guarda el contenido y devuelve un identificador opaco para recuperarlo después.</summary>
    Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre el archivo descifrado. El flujo devuelto retiene memoria del presupuesto del proceso
    /// (<see cref="IPresupuestoMemoriaDocumentos"/>) hasta que se cierra: quien lo abre lo cierra
    /// en todos los caminos (<c>await using</c>, o <c>Results.File</c>, que lo cierra al servirlo),
    /// y conviene cerrarlo en cuanto se ha copiado.
    /// </summary>
    /// <exception cref="FileNotFoundException">No existe o no es del Tenant activo.</exception>
    /// <exception cref="PresupuestoMemoriaAgotadoException">Sin hueco en el presupuesto de memoria: transitorio.</exception>
    Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default);

    /// <summary>
    /// Como <see cref="AbrirAsync"/>, pero solo si el archivo no pasa de <paramref name="maximoBytes"/> en claro. La
    /// implementación real lo comprueba con el tamaño almacenado, antes de reservar memoria, leer o descifrar nada
    /// (OWASP API4:2023): un archivo que no cabe no llega a cargarse. Esta implementación por defecto, que solo usan los
    /// dobles de prueba, abre y mide después.
    /// </summary>
    /// <exception cref="ArchivoAlmacenadoDemasiadoGrandeException">Pasa de <paramref name="maximoBytes"/>.</exception>
    /// <exception cref="PresupuestoMemoriaAgotadoException">
    /// Sin hueco ahora mismo en el presupuesto de memoria: a diferencia de <see cref="AbrirAsync"/>, la implementación real no
    /// espera hueco, porque quien abre con tope retiene ya otras reservas de la misma operación.
    /// </exception>
    async Task<Stream> AbrirHastaAsync(string identificador, long maximoBytes, CancellationToken cancellationToken = default)
    {
        var flujo = await AbrirAsync(identificador, cancellationToken);
        if (!flujo.CanSeek || flujo.Length <= maximoBytes)
            return flujo;

        await flujo.DisposeAsync();
        throw new ArchivoAlmacenadoDemasiadoGrandeException();
    }

    /// <summary>
    /// Borra el archivo de forma definitiva. Existe para la supresión por
    /// retención (Project-Hydra-Negocio/tecnico/RGPD-TRATAMIENTO-DATOS.md § 5): el dato personal de un
    /// Documento está dentro del PDF, así que limpiar la fila sin borrar el
    /// fichero dejaría la supresión en apariencia.
    ///
    /// No falla si el archivo ya no está: la operación describe un estado
    /// final —"que no exista"—, y hacerla fallar por eso obligaría a cada
    /// llamador a distinguir un caso que le da igual.
    /// </summary>
    Task EliminarAsync(string identificador, CancellationToken cancellationToken = default);
}

/// <summary>El archivo almacenado pasa del tope que pidió quien lo abre (<see cref="IFileStorageService.AbrirHastaAsync"/>).</summary>
public sealed class ArchivoAlmacenadoDemasiadoGrandeException()
    : InvalidOperationException("El archivo almacenado pasa del tamaño máximo que se puede abrir aquí.");
