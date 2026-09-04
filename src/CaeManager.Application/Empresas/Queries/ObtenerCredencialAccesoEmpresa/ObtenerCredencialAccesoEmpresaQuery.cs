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
        // Alcance de GESTIÓN, no de lectura (REC-153): la cartera de lectura de
        // un usuario de portal (rol Cliente) incluye las contratistas de su
        // propio Cliente —es lo que ese portal existe para enseñar—, pero la
        // credencial de acceso a la plataforma externa de esa contratista es un
        // artefacto interno, no documentación. Con el alcance de lectura
        // (ObtenerEmpresaIdsVisiblesAsync) como puerta, ese usuario recibía la
        // contraseña en claro de una Empresa que solo debía poder consultar
        // como cliente, nunca administrar.
        //
        // Fuera de la cartera se responde como si no existiera: confirmar que la
        // fila existe ya seria decir algo sobre datos que no corresponden.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
            return null;

        return await dbContext.CredencialesAccesoEmpresa
            .Where(c => c.EmpresaId == request.EmpresaId)
            .Select(c => new CredencialAccesoEmpresaDto(c.UrlAcceso, c.CampoEmpresa, c.Usuario, c.Contrasena, c.Notas))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
