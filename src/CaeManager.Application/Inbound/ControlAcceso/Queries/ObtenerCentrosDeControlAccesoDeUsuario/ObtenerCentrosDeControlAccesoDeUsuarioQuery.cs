using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Inbound.ControlAcceso.Queries.ObtenerMisCentrosDeControlAcceso;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Inbound.ControlAcceso;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ControlAcceso.Queries.ObtenerCentrosDeControlAccesoDeUsuario;

/// <summary>
/// Los Centros asignados a una persona con el rol «Control de acceso», para el drawer de asignación de la gestión de usuarios
/// (AF-01). Pide <see cref="CapacidadInbound.GestionarInbound"/>, como el comando que los cambia; sin ella, lista vacía. Solo
/// devuelve Centros del Tenant activo que siguen existiendo.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerCentrosDeControlAccesoDeUsuarioQuery(Guid UsuarioId) : IRequest<IReadOnlyList<CentroDeControlAccesoDto>>;

public class ObtenerCentrosDeControlAccesoDeUsuarioQueryHandler(
    IUsuarioControlAccesoCentroRepository repositorio,
    ICentrosQueryContext centros,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerCentrosDeControlAccesoDeUsuarioQuery, IReadOnlyList<CentroDeControlAccesoDto>>
{
    public async Task<IReadOnlyList<CentroDeControlAccesoDto>> Handle(
        ObtenerCentrosDeControlAccesoDeUsuarioQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.GestionarInbound, cancellationToken))
            return [];

        var asignados = (await repositorio.ObtenerDeUsuarioAsync(request.UsuarioId, cancellationToken))
            .Select(a => a.CentroId)
            .ToList();
        if (asignados.Count == 0)
            return [];

        return await centros.Centros
            .Where(c => asignados.Contains(c.Id))
            .OrderBy(c => c.Nombre)
            .Select(c => new CentroDeControlAccesoDto(c.Id, c.Nombre))
            .ToListAsync(cancellationToken);
    }
}
