using CaeManager.Application.Common;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries.ObtenerVehiculosConActividadDeCentro;

/// <summary>
/// Respalda la pestaña "Vehículos" del Context Workspace de Centro.
/// Vehiculo no tiene relación directa con Centro ni con Trabajador en el
/// modelo (decisión ya tomada en Fase 29 de ROADMAP.md al no conectar
/// Vehiculo con Visita, por el mismo motivo: no se sabe de antemano qué
/// vehículo concreto usa cada trabajador). Se muestra como información
/// contextual: los vehículos de las Empresas/Subcontratas que tienen al
/// menos un Trabajador con asignación activa en este Centro.
/// </summary>
public record ObtenerVehiculosConActividadDeCentroQuery(Guid CentroId) : IRequest<IReadOnlyList<VehiculoConActividadDto>>;

public record VehiculoConActividadDto(Guid Id, string Nombre, string Modelo, string NumeroPlaca, string EmpleadorNombre);

public class ObtenerVehiculosConActividadDeCentroQueryHandler(IAsignacionesQueryContext asignacionesContext, IEmpresasQueryContext empresasContext, ISubcontratasQueryContext subcontratasContext, ITrabajadoresQueryContext trabajadoresContext, IVehiculosQueryContext vehiculosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVehiculosConActividadDeCentroQuery, IReadOnlyList<VehiculoConActividadDto>>
{
    public async Task<IReadOnlyList<VehiculoConActividadDto>> Handle(
        ObtenerVehiculosConActividadDeCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        var empleadores = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.CentroId == request.CentroId && asignacion.FechaBaja == null
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new { trabajador.EmpresaId, trabajador.SubcontrataId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var empresaIds = empleadores.Where(e => e.EmpresaId != null).Select(e => e.EmpresaId!.Value).Distinct().ToList();
        var subcontrataIds = empleadores.Where(e => e.SubcontrataId != null).Select(e => e.SubcontrataId!.Value).Distinct().ToList();

        if (empresaIds.Count == 0 && subcontrataIds.Count == 0)
            return [];

        return await (
            from vehiculo in vehiculosContext.Vehiculos
            where (vehiculo.EmpresaId != null && empresaIds.Contains(vehiculo.EmpresaId.Value))
               || (vehiculo.SubcontrataId != null && subcontrataIds.Contains(vehiculo.SubcontrataId.Value))
            join empresa in empresasContext.Empresas on vehiculo.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in subcontratasContext.Subcontratas on vehiculo.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            orderby vehiculo.Nombre
            select new VehiculoConActividadDto(
                vehiculo.Id, vehiculo.Nombre, vehiculo.Modelo, vehiculo.NumeroPlaca,
                empresa != null ? empresa.RazonSocial : subcontrata!.RazonSocial))
            .ToListAsync(cancellationToken);
    }
}
