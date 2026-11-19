using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Inbound.ControlAcceso;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ControlAcceso.Queries.ObtenerMisCentrosDeControlAcceso;

/// <summary>Un Centro en el que la persona opera la portería. Solo identificador y nombre: sin contratos, requisitos ni Trabajadores.</summary>
public record CentroDeControlAccesoDto(Guid CentroId, string Nombre);

/// <summary>
/// Los Centros de portería de quien consulta (AF-01): solo los que el Gestor CAE le asignó (<c>UsuarioControlAccesoCentro</c>) y que
/// siguen existiendo en el Tenant activo. Sin <see cref="CapacidadInbound.ControlarAcceso"/>, sin usuario o sin asignaciones,
/// lista vacía, sin decir cuál de las condiciones falla; un Centro del mismo Tenant que no le asignaron nunca aparece. Es el
/// usuario actual, nunca un parámetro: nadie pregunta por los Centros de otro.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerMisCentrosDeControlAccesoQuery : IRequest<IReadOnlyList<CentroDeControlAccesoDto>>;

public class ObtenerMisCentrosDeControlAccesoQueryHandler(
    IUsuarioControlAccesoCentroRepository repositorio,
    ICentrosQueryContext centros,
    ICurrentUserService currentUserService,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerMisCentrosDeControlAccesoQuery, IReadOnlyList<CentroDeControlAccesoDto>>
{
    public async Task<IReadOnlyList<CentroDeControlAccesoDto>> Handle(
        ObtenerMisCentrosDeControlAccesoQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ControlarAcceso, cancellationToken))
            return [];

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return [];

        var asignados = (await repositorio.ObtenerDeUsuarioAsync(usuarioId, cancellationToken))
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
