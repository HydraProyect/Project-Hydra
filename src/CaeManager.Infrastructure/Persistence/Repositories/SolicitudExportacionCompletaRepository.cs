using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Exportacion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="ISolicitudExportacionCompletaRepository"/>
public class SolicitudExportacionCompletaRepository(CaeManagerDbContext dbContext) : ISolicitudExportacionCompletaRepository
{
    public Task<bool> ExisteQueCuentaParaElLimiteDesdeAsync(DateTime desdeUtc, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesExportacionCompleta.AnyAsync(
            s => s.SolicitadaEnUtc > desdeUtc && s.Estado != EstadoSolicitudExportacionCompleta.Fallida,
            cancellationToken);

    public Task<SolicitudExportacionCompleta?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesExportacionCompleta.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Agregar(SolicitudExportacionCompleta solicitud) => dbContext.SolicitudesExportacionCompleta.Add(solicitud);

    public void AgregarAuditoria(RegistroAuditoria registro) => dbContext.RegistrosAuditoria.Add(registro);
}
