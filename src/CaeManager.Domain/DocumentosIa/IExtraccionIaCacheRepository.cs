namespace CaeManager.Domain.DocumentosIa;

public interface IExtraccionIaCacheRepository
{
    /// <summary>
    /// Busca por la clave completa, no solo por el hash: la entrada guarda una
    /// interpretación del archivo bajo un tipo esperado y una versión del
    /// pipeline concretos, y servirla para otra pregunta o para otra versión
    /// devolvería una respuesta que nadie llegó a calcular. Ver
    /// <see cref="ExtraccionIaCache"/>.
    /// </summary>
    Task<ExtraccionIaCache?> ObtenerAsync(string hashSha256, string tipoEsperado, CancellationToken cancellationToken = default);

    void Agregar(ExtraccionIaCache cache);
}
