namespace CaeManager.Domain.Documentos;

public interface IConfiguracionIaDocumentoClienteRepository
{
    Task<ConfiguracionIaDocumentoCliente?> ObtenerAsync(Guid clienteId, Guid tipoDocumentoId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfiguracionIaDocumentoCliente>> ObtenerPorClienteAsync(Guid clienteId, CancellationToken cancellationToken = default);

    /// <summary>Los TipoDocumentoId con lectura IA desactivada para este Cliente, ya sea por este override o por el interruptor global (nivel 1) — usado al reasignar Ejecutivo para saber si avisar al nuevo Gestor.</summary>
    Task<IReadOnlyList<string>> ObtenerNombresTiposDocumentoSinLecturaIaAsync(Guid clienteId, CancellationToken cancellationToken = default);

    void Agregar(ConfiguracionIaDocumentoCliente configuracion);

    void Eliminar(ConfiguracionIaDocumentoCliente configuracion);
}
