using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPendientes;

/// <summary>
/// Detecciones pendientes (sin resolver) de todas las Empresas visibles para
/// el usuario actual — alimenta el badge de Empresas y el tipo
/// DeteccionPendiente de la Bandeja del gestor (Centro 360,
/// docs/ux-audit/03-empresas-subcontratas.md H2): sin esta cola persistente,
/// la única referencia a una detección pendiente era la notificación de
/// campana, que se pierde si se descarta.
/// </summary>
public record ObtenerDeteccionesPendientesQuery : IRequest<IReadOnlyList<DeteccionPendienteDto>>;

public record DeteccionPendienteDto(
    Guid Id, Guid EmpresaId, string EmpresaRazonSocial, TipoDeteccion Tipo, string NombreCompleto, DateTime CreadaEnUtc);

public class ObtenerDeteccionesPendientesQueryHandler(
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDeteccionesPendientesQuery, IReadOnlyList<DeteccionPendienteDto>>
{
    public async Task<IReadOnlyList<DeteccionPendienteDto>> Handle(
        ObtenerDeteccionesPendientesQuery request, CancellationToken cancellationToken)
    {
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);

        var consulta =
            from deteccion in trabajadoresContext.DeteccionesTrabajador
            where !deteccion.Resuelta
            join empresa in empresasContext.Empresas on deteccion.EmpresaId equals empresa.Id
            select new { deteccion, empresa };

        if (empresaIdsVisibles is not null)
            consulta = consulta.Where(x => empresaIdsVisibles.Contains(x.empresa.Id));

        return await consulta
            .OrderByDescending(x => x.deteccion.CreadaEnUtc)
            .Select(x => new DeteccionPendienteDto(
                x.deteccion.Id, x.empresa.Id, x.empresa.RazonSocial, x.deteccion.Tipo,
                x.deteccion.Nombre + " " + x.deteccion.Apellidos, x.deteccion.CreadaEnUtc))
            .ToListAsync(cancellationToken);
    }
}
