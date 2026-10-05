using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles;

/// <summary>
/// Opciones de la pastilla «Empresa» de /trabajadores: las Empresas y Subcontratas que emplean a algún
/// Trabajador que quien mira ve. Mismo alcance que la lista (<see cref="IAlcanceDatosService.ObtenerTrabajadorIdsVisiblesAsync"/>,
/// el de <c>ObtenerTrabajadoresQuery</c>): cada opción devuelve al menos un Trabajador y ninguna nombra un
/// empleador que la lista no muestre ya en su columna «Empresa».
///
/// No sirven los selectores del alta: <c>ObtenerEmpresasParaSelectorQuery</c> acota por alcance de
/// <b>gestión</b> (vacío para un usuario de portal, rol Cliente, que sí ve Trabajadores por Asignación) y
/// <c>ObtenerSubcontratasParaSelectorQuery</c> devuelve el catálogo global del Tenant a propósito (para reutilizar
/// un registro al dar de alta): en un filtro le enseñaba al portal subcontratas que no tienen nada que ver con él.
/// Solo lectura para filtrar: no alimenta ningún comando.
/// </summary>
public record ObtenerEmpleadoresDeTrabajadoresVisiblesQuery : IRequest<EmpleadoresDeTrabajadoresDto>;

public record EmpleadorDeTrabajadorDto(Guid Id, string RazonSocial);

public record EmpleadoresDeTrabajadoresDto(
    IReadOnlyList<EmpleadorDeTrabajadorDto> Empresas, IReadOnlyList<EmpleadorDeTrabajadorDto> Subcontratas);

public class ObtenerEmpleadoresDeTrabajadoresVisiblesQueryHandler(
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEmpleadoresDeTrabajadoresVisiblesQuery, EmpleadoresDeTrabajadoresDto>
{
    public async Task<EmpleadoresDeTrabajadoresDto> Handle(
        ObtenerEmpleadoresDeTrabajadoresVisiblesQuery request, CancellationToken cancellationToken)
    {
        var trabajadores = trabajadoresContext.Trabajadores.AsQueryable();

        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            trabajadores = trabajadores.Where(t => trabajadorIdsVisibles.Contains(t.Id));

        // El join con Empresas (filtro global de Tenant y de bajas lógicas) deja fuera a un empleador dado de baja,
        // igual que la lista, que lo une para pintar su nombre.
        var empresaIds = trabajadores.Where(t => t.EmpresaId != null).Select(t => t.EmpresaId!.Value).Distinct();
        var subcontrataIds = trabajadores.Where(t => t.SubcontrataId != null).Select(t => t.SubcontrataId!.Value).Distinct();

        var empresas = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .OrderBy(e => e.RazonSocial).ThenBy(e => e.Id)
            .Select(e => new EmpleadorDeTrabajadorDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);

        var subcontratas = await empresasContext.Empresas
            .Where(e => subcontrataIds.Contains(e.Id))
            .OrderBy(e => e.RazonSocial).ThenBy(e => e.Id)
            .Select(e => new EmpleadorDeTrabajadorDto(e.Id, e.RazonSocial))
            .ToListAsync(cancellationToken);

        return new EmpleadoresDeTrabajadoresDto(empresas, subcontratas);
    }
}
