using CaeManager.Domain.Operaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Persistence.Repositories;

/// <summary>
/// Los listados van sin seguimiento: alimentan la campana y «Dar acceso», que se recargan en
/// un contexto de circuito de larga vida, y una entidad seguida devolvería el estado de la
/// primera lectura aunque el destinatario ya hubiera respondido. <see cref="ObtenerPorIdAsync"/>
/// sí la sigue: la carga un Command para cambiarla, y su versión detecta la carrera.
/// </summary>
public class PropuestaApoyoCarteraRepository(CaeManagerDbContext dbContext) : IPropuestaApoyoCarteraRepository
{
    public Task<PropuestaApoyoCartera?> ObtenerPorIdAsync(
        Guid id, Guid operadorTenantId, CancellationToken cancellationToken = default) =>
        dbContext.PropuestasApoyoCartera
            .FirstOrDefaultAsync(p => p.Id == id && p.OperadorTenantId == operadorTenantId, cancellationToken);

    public async Task<IReadOnlyList<PropuestaApoyoCartera>> ListarPendientesDelDestinatarioAsync(
        Guid operadorTenantId, Guid destinatarioUsuarioId, CancellationToken cancellationToken = default) =>
        await dbContext.PropuestasApoyoCartera
            .AsNoTracking()
            .Where(p => p.OperadorTenantId == operadorTenantId
                        && p.DestinatarioUsuarioId == destinatarioUsuarioId
                        && p.Estado == EstadoPropuestaApoyoCartera.Pendiente)
            .OrderBy(p => p.CreadaEnUtc)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<PropuestaApoyoCartera>> ListarPendientesDelProponenteAsync(
        Guid operadorTenantId, Guid proponenteUsuarioId, CancellationToken cancellationToken = default) =>
        await dbContext.PropuestasApoyoCartera
            .AsNoTracking()
            .Where(p => p.OperadorTenantId == operadorTenantId
                        && p.ProponenteUsuarioId == proponenteUsuarioId
                        && p.Estado == EstadoPropuestaApoyoCartera.Pendiente)
            .OrderBy(p => p.CreadaEnUtc)
            .ToListAsync(cancellationToken);

    public Task<bool> ExistePendienteAsync(
        Guid asignacionOperacionId, Guid destinatarioUsuarioId, CancellationToken cancellationToken = default) =>
        dbContext.PropuestasApoyoCartera.AnyAsync(
            p => p.AsignacionOperacionId == asignacionOperacionId
                 && p.DestinatarioUsuarioId == destinatarioUsuarioId
                 && p.Estado == EstadoPropuestaApoyoCartera.Pendiente, cancellationToken);

    public void Agregar(PropuestaApoyoCartera propuesta) => dbContext.PropuestasApoyoCartera.Add(propuesta);
}
