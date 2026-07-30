using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCanalGestionDeCentro;

/// <summary>
/// Respalda la pestaña "Plataforma" del Context Workspace. Deliberadamente
/// NO proyecta Usuario/Contrasena (dato cifrado en reposo, ver
/// ARCHITECTURE.md — "el acceso a verlas está restringido por policy y
/// queda registrado en auditoría como acceso a dato sensible", mecanismo
/// que no existe todavía) — solo expone <see cref="CanalGestionResumenDto.TieneCredenciales"/>.
/// </summary>
public record ObtenerCanalGestionDeCentroQuery(Guid CentroId) : IRequest<CanalGestionResumenDto?>;

public record CanalGestionResumenDto(
    Guid Id,
    TipoCanalGestion Tipo,
    string? NombrePlataforma,
    string? UrlAcceso,
    string? EmailsDestinatarios,
    string? NombreContacto,
    string? Notas,
    bool TieneCredenciales);

public class ObtenerCanalGestionDeCentroQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCanalGestionDeCentroQuery, CanalGestionResumenDto?>
{
    public async Task<CanalGestionResumenDto?> Handle(
        ObtenerCanalGestionDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        return await dbContext.CanalesGestionDocumental
            .Where(c => c.CentroId == request.CentroId)
            .Select(c => new CanalGestionResumenDto(
                c.Id, c.Tipo, c.NombrePlataforma, c.UrlAcceso, c.EmailsDestinatarios, c.NombreContacto, c.Notas, c.Usuario != null))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
