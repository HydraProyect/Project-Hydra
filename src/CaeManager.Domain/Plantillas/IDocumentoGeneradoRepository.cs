namespace CaeManager.Domain.Plantillas;

public interface IDocumentoGeneradoRepository
{
    void Agregar(DocumentoGenerado documentoGenerado);

    /// <summary>
    /// Materializa la entidad entera, <see cref="DocumentoGenerado.CodigoSeguroVerificacion"/> incluido:
    /// en Sesión Privilegiada falla con 42501, porque Soporte TALVEG no tiene esa columna. Una lectura
    /// alcanzable en Sesión Privilegiada proyecta las columnas que necesita.
    /// </summary>
    Task<DocumentoGenerado?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);
}
