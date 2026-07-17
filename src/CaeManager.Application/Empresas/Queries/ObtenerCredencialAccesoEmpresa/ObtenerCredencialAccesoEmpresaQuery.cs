using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;

public record ObtenerCredencialAccesoEmpresaQuery(Guid EmpresaId) : IRequest<CredencialAccesoEmpresaDto?>;

public record CredencialAccesoEmpresaDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena);

public class ObtenerCredencialAccesoEmpresaQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerCredencialAccesoEmpresaQuery, CredencialAccesoEmpresaDto?>
{
    public Task<CredencialAccesoEmpresaDto?> Handle(ObtenerCredencialAccesoEmpresaQuery request, CancellationToken cancellationToken) =>
        dbContext.CredencialesAccesoEmpresa
            .Where(c => c.EmpresaId == request.EmpresaId)
            .Select(c => new CredencialAccesoEmpresaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena))
            .FirstOrDefaultAsync(cancellationToken);
}
