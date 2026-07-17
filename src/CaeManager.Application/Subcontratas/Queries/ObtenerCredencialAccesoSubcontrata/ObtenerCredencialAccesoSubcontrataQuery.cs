using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;

public record ObtenerCredencialAccesoSubcontrataQuery(Guid SubcontrataId) : IRequest<CredencialAccesoSubcontrataDto?>;

public record CredencialAccesoSubcontrataDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena);

public class ObtenerCredencialAccesoSubcontrataQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerCredencialAccesoSubcontrataQuery, CredencialAccesoSubcontrataDto?>
{
    public Task<CredencialAccesoSubcontrataDto?> Handle(ObtenerCredencialAccesoSubcontrataQuery request, CancellationToken cancellationToken) =>
        dbContext.CredencialesAccesoSubcontrata
            .Where(c => c.SubcontrataId == request.SubcontrataId)
            .Select(c => new CredencialAccesoSubcontrataDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena))
            .FirstOrDefaultAsync(cancellationToken);
}
