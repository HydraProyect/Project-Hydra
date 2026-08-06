using CaeManager.Application.Common;
using CaeManager.Application.Centros;
using CaeManager.Domain.Centros;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;

/// <summary>
/// Respalda la pestaña "Plataforma" del Context Workspace. Devuelve los N
/// canales del Centro (PLAN-EJECUCION-UX.md § 0.6, Lote 0-E — antes era uno
/// solo), con el principal primero.
///
/// Deliberadamente NO proyecta Usuario/Contrasena (dato cifrado en reposo, ver
/// ARCHITECTURE.md — "el acceso a verlas está restringido por policy y queda
/// registrado en auditoría como acceso a dato sensible", mecanismo que no
/// existe todavía) — solo expone <see cref="CanalGestionResumenDto.TieneCredenciales"/>.
/// </summary>
public record ObtenerCanalesGestionDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<CanalGestionResumenDto>>;

public record CanalGestionResumenDto(
    Guid Id,
    TipoCanalGestion Tipo,
    string EtiquetaProposito,
    bool EsPrincipal,
    string? NombrePlataforma,
    string? UrlAcceso,
    string? EmailsDestinatarios,
    string? NombreContacto,
    string? Notas,
    bool TieneCredenciales,
    Guid Version);

public class ObtenerCanalesGestionDeCentroQueryHandler(ICentrosQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerCanalesGestionDeCentroQuery, IReadOnlyList<CanalGestionResumenDto>>
{
    public async Task<IReadOnlyList<CanalGestionResumenDto>> Handle(
        ObtenerCanalesGestionDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        return await dbContext.CanalesGestionDocumental
            .Where(c => c.CentroId == request.CentroId)
            .OrderByDescending(c => c.EsPrincipal)
            .ThenBy(c => c.EtiquetaProposito)
            .Select(c => new CanalGestionResumenDto(
                c.Id, c.Tipo, c.EtiquetaProposito, c.EsPrincipal, c.NombrePlataforma, c.UrlAcceso,
                c.EmailsDestinatarios, c.NombreContacto, c.Notas, c.Usuario != null, c.Version))
            .ToListAsync(cancellationToken);
    }
}
