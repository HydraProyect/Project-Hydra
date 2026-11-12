namespace CaeManager.Domain.Trabajadores;

public interface ITrabajadorRepository
{
    Task<Trabajador?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Los Trabajadores no eliminados con identidad (DNI/NIE) del Tenant en curso, releídos de la base los que el contexto
    /// ya rastreaba: quien decide bajo el candado de la regla A-1 tiene que ver lo que otra operación confirmó mientras
    /// esperaba.
    /// </summary>
    Task<IReadOnlyList<Trabajador>> ListarConIdentidadActualizadosAsync(CancellationToken cancellationToken = default);

    Task<bool> ExisteConDniAsync(string dni, Guid? excluirId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// De los DNI dados (ya normalizados), los de Trabajadores del Tenant propietario que están en la Papelera.
    /// <c>IX_Trabajadores_TenantId_Dni</c> incluye a los eliminados: una alta con uno de ellos haría fallar el guardado
    /// entero (23505). Devuelve solo los DNI pedidos, sin ningún otro dato del eliminado; sin Tenant propietario resuelto
    /// no devuelve ninguno.
    /// </summary>
    Task<IReadOnlySet<string>> ObtenerDnisEnPapeleraAsync(IReadOnlyCollection<string> dnis, CancellationToken cancellationToken = default);

    void Agregar(Trabajador trabajador);
}
