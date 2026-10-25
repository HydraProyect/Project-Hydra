using CaeManager.Application.Centros;

namespace CaeManager.Application.Tests.Centros;

/// <summary>Para los tests de listados que no miran el estado: ningún requisito obligatorio incumplido.</summary>
public sealed class RequisitosObligatoriosSinIncumplidos : IRequisitosObligatoriosIncumplidosService
{
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeEmpresasAsync(
        IQueryable<Guid> empresaIds, CancellationToken cancellationToken) =>
        Task.FromResult(RequisitosObligatoriosIncumplidos.Ninguno.PorEmpresa);

    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeTrabajadoresAsync(
        IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken) =>
        Task.FromResult(RequisitosObligatoriosIncumplidos.Ninguno.PorTrabajador);

    public Task<RequisitosObligatoriosIncumplidos> DeEmpresasYTrabajadoresAsync(
        IQueryable<Guid> empresaIds, IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken) =>
        Task.FromResult(RequisitosObligatoriosIncumplidos.Ninguno);
}
