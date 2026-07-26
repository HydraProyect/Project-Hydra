using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;

/// <summary>Respalda la pestaña "Trabajadores" del Context Workspace de Centro — solo asignaciones activas (FechaBaja null).</summary>
public record ObtenerTrabajadoresAsignadosDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<TrabajadorAsignadoDto>>;

public record TrabajadorAsignadoDto(Guid TrabajadorId, string Nombre, string EmpleadorNombre, DateOnly FechaAlta);

public class ObtenerTrabajadoresAsignadosDeCentroQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTrabajadoresAsignadosDeCentroQuery, IReadOnlyList<TrabajadorAsignadoDto>>
{
    public async Task<IReadOnlyList<TrabajadorAsignadoDto>> Handle(
        ObtenerTrabajadoresAsignadosDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        return await (
            from asignacion in dbContext.Asignaciones
            where asignacion.CentroId == request.CentroId && asignacion.FechaBaja == null
            join trabajador in dbContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join empresa in dbContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in dbContext.Subcontratas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            orderby trabajador.Nombre
            select new TrabajadorAsignadoDto(
                trabajador.Id,
                trabajador.Nombre + " " + trabajador.Apellidos,
                empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial,
                asignacion.FechaAlta))
            .ToListAsync(cancellationToken);
    }
}
