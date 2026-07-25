using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerPlataformaAccesoDeCentro;

/// <summary>
/// Respalda la pestaña "Plataforma" del Context Workspace. Deliberadamente
/// NO proyecta Usuario/Contrasena (dato cifrado en reposo, ver
/// ARCHITECTURE.md — "el acceso a verlas está restringido por policy y
/// queda registrado en auditoría como acceso a dato sensible", mecanismo
/// que no existe todavía) — solo expone <see cref="PlataformaAccesoResumenDto.TieneCredenciales"/>.
/// Ver el módulo de Centros (Drawer de edición) para gestionar las
/// credenciales completas.
/// </summary>
public record ObtenerPlataformaAccesoDeCentroQuery(Guid CentroId) : IRequest<PlataformaAccesoResumenDto?>;

public record PlataformaAccesoResumenDto(
    Guid Id, string NombrePlataforma, string? UrlAcceso, string? Notas, bool TieneCredenciales);

public class ObtenerPlataformaAccesoDeCentroQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPlataformaAccesoDeCentroQuery, PlataformaAccesoResumenDto?>
{
    public async Task<PlataformaAccesoResumenDto?> Handle(
        ObtenerPlataformaAccesoDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return null;

        return await dbContext.PlataformasAcceso
            .Where(p => p.CentroId == request.CentroId)
            .Select(p => new PlataformaAccesoResumenDto(p.Id, p.NombrePlataforma, p.UrlAcceso, p.Notas, p.Usuario != null))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
