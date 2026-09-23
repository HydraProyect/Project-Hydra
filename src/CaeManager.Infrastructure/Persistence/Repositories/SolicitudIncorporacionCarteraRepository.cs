using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Los listados van sin seguimiento: alimentan la bandeja y el aviso, que se
/// recargan en un contexto de circuito de larga vida, y una entidad seguida
/// devolvería el estado de la primera lectura aunque otro Coordinador CAE ya
/// la hubiera resuelto. <see cref="ObtenerPorIdAsync"/> sí la sigue: la
/// carga un Command para modificarla, y su versión detecta la carrera.
/// </summary>
public class SolicitudIncorporacionCarteraRepository(CaeManagerDbContext dbContext) : ISolicitudIncorporacionCarteraRepository
{
    public Task<SolicitudIncorporacionCartera?> ObtenerPorIdAsync(
        Guid id, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesIncorporacionCartera
            .FirstOrDefaultAsync(s => s.Id == id && s.OperadorTenantId == operadorTenantId, cancellationToken);

    public async Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarPendientesAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        await dbContext.SolicitudesIncorporacionCartera
            .AsNoTracking()
            .Where(s => s.OperadorTenantId == operadorTenantId && s.Estado == EstadoSolicitudIncorporacionCartera.Pendiente)
            .OrderBy(s => s.CreadaEnUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarAceptadasAsync(
        Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        await dbContext.SolicitudesIncorporacionCartera
            .AsNoTracking()
            .Where(s => s.OperadorTenantId == operadorTenantId && s.Estado == EstadoSolicitudIncorporacionCartera.Aceptada)
            .OrderByDescending(s => s.ResueltaEnUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SolicitudIncorporacionCartera>> ListarDelSolicitanteAsync(
        Guid operadorTenantId, Guid solicitanteUsuarioId, CancellationToken cancellationToken = default) =>
        await dbContext.SolicitudesIncorporacionCartera
            .AsNoTracking()
            .Where(s => s.OperadorTenantId == operadorTenantId && s.SolicitanteUsuarioId == solicitanteUsuarioId)
            .OrderByDescending(s => s.CreadaEnUtc)
            .ToListAsync(cancellationToken);

    public Task<bool> ExistePendienteAsync(
        Guid asignacionOperacionId, Guid solicitanteUsuarioId, CancellationToken cancellationToken = default) =>
        dbContext.SolicitudesIncorporacionCartera.AnyAsync(
            s => s.AsignacionOperacionId == asignacionOperacionId
                 && s.SolicitanteUsuarioId == solicitanteUsuarioId
                 && s.Estado == EstadoSolicitudIncorporacionCartera.Pendiente, cancellationToken);

    public void Agregar(SolicitudIncorporacionCartera solicitud) => dbContext.SolicitudesIncorporacionCartera.Add(solicitud);
}
