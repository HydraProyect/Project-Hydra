namespace CaeManager.Domain.Cumplimiento;

public interface IInstruccionTratamientoIaTenantPropietarioRepository
{
    /// <summary>
    /// La fila vigente (no revocada) del tenant en contexto — a lo sumo una,
    /// aunque el histórico append-only pueda tener varias ya cerradas. No es
    /// solo una expectativa: <c>InstruccionTratamientoIaTenantPropietarioConfiguration</c>
    /// la fuerza con un índice único filtrado por <c>TenantId</c> donde
    /// <c>RevocadaEnUtc IS NULL</c> — sin él, dos altas concurrentes podrían
    /// pasar ambas el check-then-insert del comando de registro, y qué fila
    /// gobernaría el gate de Nivel 0 quedaría indeterminado.
    /// </summary>
    Task<InstruccionTratamientoIaTenantPropietario?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Histórico completo, más reciente primero — lo que demuestra "qué aceptó y cuándo" (criterio de aceptación de HO-035-02).</summary>
    Task<IReadOnlyList<InstruccionTratamientoIaTenantPropietario>> ObtenerHistoricoAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<InstruccionTratamientoIaTenantPropietario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Agregar(InstruccionTratamientoIaTenantPropietario instruccion);
}
