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
        // Alcance de GESTIÓN, no de lectura (REC-159, gemelo de REC-153): la
        // credencial de acceso al portal de la Subcontrata es un artefacto
        // interno de gestión, no documentación del propio Cliente. La cartera
        // de lectura de un usuario de portal (rol Cliente) SÍ incluye las
        // subcontratas de su Cliente —es lo que el portal existe para
        // enseñar—, así que usarla aquí filtraba la contraseña en claro a
        // ese mismo usuario.
        //
        // Fuera de la cartera se responde como si no existiera: confirmar que la
        // fila existe ya seria decir algo sobre datos que no corresponden.
        if (!await alcanceDatos.SubcontrataParaGestionVisibleAsync(request.SubcontrataId, cancellationToken))
            return null;

        return await dbContext.CredencialesAccesoSubcontrata
            .Where(c => c.SubcontrataId == request.SubcontrataId)
            .Select(c => new CredencialAccesoSubcontrataDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
