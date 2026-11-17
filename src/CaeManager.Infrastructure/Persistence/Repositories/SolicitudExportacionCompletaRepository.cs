using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Exportacion;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <inheritdoc cref="ISolicitudExportacionCompletaRepository"/>
public class SolicitudExportacionCompletaRepository(CaeManagerDbContext dbContext) : ISolicitudExportacionCompletaRepository
{
    // Sin filtro de estado: también cuentan las fallidas (D-8, OWASP API4:2023).
    public Task<bool> ExisteRegistradaDesdeAsync(DateTime desdeUtc, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesExportacionCompleta.AnyAsync(s => s.SolicitadaEnUtc > desdeUtc, cancellationToken);

    public Task<SolicitudExportacionCompleta?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesExportacionCompleta.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public void Agregar(SolicitudExportacionCompleta solicitud) => dbContext.SolicitudesExportacionCompleta.Add(solicitud);

    public void AgregarAuditoria(RegistroAuditoria registro) => dbContext.RegistrosAuditoria.Add(registro);
}
