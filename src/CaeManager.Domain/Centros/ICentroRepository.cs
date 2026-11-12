namespace CaeManager.Domain.Centros;

public interface ICentroRepository
{
    Task<Centro?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// El Centro del Tenant indicado aunque esté eliminado (Papelera), releído de la base si el contexto ya lo rastreaba:
    /// quien decide bajo el candado de la regla A-1 tiene que ver lo que otra operación confirmó mientras esperaba.
    /// </summary>
    Task<Centro?> ObtenerIncluidoEliminadoActualizadoAsync(Guid id, Guid? tenantId, CancellationToken cancellationToken = default);

    /// <summary>Los Centros no eliminados del Tenant en curso, releídos de la base los que el contexto ya rastreaba.</summary>
    Task<IReadOnlyList<Centro>> ListarActualizadosAsync(CancellationToken cancellationToken = default);

    /// <summary>El nombre de un Centro es único dentro de su Cliente, no globalmente.</summary>
    Task<bool> ExisteConNombreEnClienteAsync(
        Guid clienteId, string nombre, Guid? excluirId = null, CancellationToken cancellationToken = default);

    void Agregar(Centro centro);
}
