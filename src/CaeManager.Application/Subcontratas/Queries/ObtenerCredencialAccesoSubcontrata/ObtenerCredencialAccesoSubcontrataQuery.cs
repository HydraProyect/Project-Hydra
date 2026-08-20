using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;

public record ObtenerCredencialAccesoSubcontrataQuery(Guid SubcontrataId)
    : IRequest<CredencialAccesoSubcontrataDto?>, IConsultaDeSecretosDeTenant;

public record CredencialAccesoSubcontrataDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena, string? Notas);

public class ObtenerCredencialAccesoSubcontrataQueryHandler(ISubcontratasQueryContext dbContext)
    : IRequestHandler<ObtenerCredencialAccesoSubcontrataQuery, CredencialAccesoSubcontrataDto?>
{
    public Task<CredencialAccesoSubcontrataDto?> Handle(ObtenerCredencialAccesoSubcontrataQuery request, CancellationToken cancellationToken) =>
        dbContext.CredencialesAccesoSubcontrata
            .Where(c => c.SubcontrataId == request.SubcontrataId)
            .Select(c => new CredencialAccesoSubcontrataDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
}
