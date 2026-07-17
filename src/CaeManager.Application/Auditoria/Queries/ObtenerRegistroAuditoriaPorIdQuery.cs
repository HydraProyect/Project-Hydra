using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Auditoria.Queries;

public record ObtenerRegistroAuditoriaPorIdQuery(Guid Id) : IRequest<RegistroAuditoriaDto?>;

public class ObtenerRegistroAuditoriaPorIdQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerRegistroAuditoriaPorIdQuery, RegistroAuditoriaDto?>
{
    public Task<RegistroAuditoriaDto?> Handle(ObtenerRegistroAuditoriaPorIdQuery request, CancellationToken cancellationToken) =>
        dbContext.RegistrosAuditoria
            .Where(r => r.Id == request.Id)
            .Select(r => new RegistroAuditoriaDto(
                r.Id, r.EntidadTipo, r.EntidadId, r.Accion, r.UsuarioId, r.FechaUtc, r.DatosAntes, r.DatosDespues))
            .FirstOrDefaultAsync(cancellationToken);
}
