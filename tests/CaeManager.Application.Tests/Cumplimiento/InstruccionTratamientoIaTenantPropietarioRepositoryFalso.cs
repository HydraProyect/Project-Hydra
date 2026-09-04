using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// Doble en memoria — deliberadamente <b>ignora el parámetro <c>tenantId</c></b>
/// en las lecturas, en vez de filtrar por <c>InstruccionTratamientoIaTenantPropietario.TenantId</c>:
/// ese campo solo lo sella <c>TenantSelladoInterceptor</c> al guardar en EF
/// real (mismo mecanismo que cualquier <see cref="Domain.Common.EntidadConTenant"/>),
/// así que en este doble en memoria toda fila que se agregue se queda con
/// <c>TenantId = Guid.Empty</c> — filtrar por él aquí solo produciría falsos
/// negativos silenciosos, no una simulación real del aislamiento. Este fake
/// representa "el estado ya visible para el tenant en el que opera el
/// handler bajo prueba" (un tenant implícito por test); el aislamiento por
/// tenant de verdad —incluida la escritura cruzada vía
/// <c>AmbitoTenantExplicito</c>— lo prueba la suite de integración contra
/// Postgres real, no esta.
/// </summary>
public class InstruccionTratamientoIaTenantPropietarioRepositoryFalso : IInstruccionTratamientoIaTenantPropietarioRepository
{
    public List<InstruccionTratamientoIaTenantPropietario> Filas { get; } = [];

    public Task<InstruccionTratamientoIaTenantPropietario?> ObtenerVigenteAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filas.FirstOrDefault(i => i.RevocadaEnUtc is null));

    public Task<IReadOnlyList<InstruccionTratamientoIaTenantPropietario>> ObtenerHistoricoAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<InstruccionTratamientoIaTenantPropietario>>(
            [.. Filas.OrderByDescending(i => i.FechaAceptacionUtc)]);

    public Task<InstruccionTratamientoIaTenantPropietario?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(Filas.FirstOrDefault(i => i.Id == id));

    public void Agregar(InstruccionTratamientoIaTenantPropietario instruccion) => Filas.Add(instruccion);
}
