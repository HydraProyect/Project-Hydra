using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerCredencialAccesoSubcontrata;

public record ObtenerCredencialAccesoSubcontrataQuery(Guid SubcontrataId)
    : IRequest<CredencialAccesoSubcontrataDto?>, IConsultaDeSecretosDeTenant;

public record CredencialAccesoSubcontrataDto(string? UrlAcceso, string? CampoEmpresa, string? Usuario, string? Contrasena, string? Notas);

public class ObtenerCredencialAccesoSubcontrataQueryHandler(
    ISubcontratasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCredencialAccesoSubcontrataQuery, CredencialAccesoSubcontrataDto?>
{
    public async Task<CredencialAccesoSubcontrataDto?> Handle(
        ObtenerCredencialAccesoSubcontrataQuery request, CancellationToken cancellationToken)
    {
        // Fuera de la cartera se responde como si no existiera: confirmar que la
        // fila existe ya seria decir algo sobre datos que no corresponden.
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.SubcontrataId, cancellationToken))
            return null;

        return await dbContext.CredencialesAccesoSubcontrata
            .Where(c => c.SubcontrataId == request.SubcontrataId)
            .Select(c => new CredencialAccesoSubcontrataDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
