using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerCredencialAccesoEmpresa;

public record ObtenerCredencialAccesoEmpresaQuery(Guid EmpresaId)
    : IRequest<CredencialAccesoEmpresaDto?>, IConsultaDeSecretosDeTenant;

public record CredencialAccesoEmpresaDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena, string? Notas);

public class ObtenerCredencialAccesoEmpresaQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCredencialAccesoEmpresaQuery, CredencialAccesoEmpresaDto?>
{
    public async Task<CredencialAccesoEmpresaDto?> Handle(
        ObtenerCredencialAccesoEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Fuera de la cartera se responde como si no existiera: confirmar que la
        // fila existe ya seria decir algo sobre datos que no corresponden.
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        return await dbContext.CredencialesAccesoEmpresa
            .Where(c => c.EmpresaId == request.EmpresaId)
            .Select(c => new CredencialAccesoEmpresaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
