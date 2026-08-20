using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;

public record ObtenerCredencialAccesoEmpresaQuery(Guid EmpresaId)
    : IRequest<CredencialAccesoEmpresaDto?>, IConsultaDeSecretosDeTenant;

public record CredencialAccesoEmpresaDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena, string? Notas);

public class ObtenerCredencialAccesoEmpresaQueryHandler(IEmpresasQueryContext dbContext)
    : IRequestHandler<ObtenerCredencialAccesoEmpresaQuery, CredencialAccesoEmpresaDto?>
{
    public Task<CredencialAccesoEmpresaDto?> Handle(ObtenerCredencialAccesoEmpresaQuery request, CancellationToken cancellationToken) =>
        dbContext.CredencialesAccesoEmpresa
            .Where(c => c.EmpresaId == request.EmpresaId)
            .Select(c => new CredencialAccesoEmpresaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
}
