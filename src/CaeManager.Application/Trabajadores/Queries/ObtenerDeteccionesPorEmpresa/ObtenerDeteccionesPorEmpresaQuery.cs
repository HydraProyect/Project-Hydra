using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;

/// <summary>Detecciones pendientes (sin resolver) de altas/bajas de personal para una Empresa — ver DeteccionTrabajadoresService.</summary>
public record ObtenerDeteccionesPorEmpresaQuery(Guid EmpresaId) : IRequest<Result<IReadOnlyList<DeteccionTrabajadorDto>>>;

public record DeteccionTrabajadorDto(
    Guid Id, TipoDeteccion Tipo, string Nombre, string Apellidos, string Dni, Guid? TrabajadorExistenteId, DateTime CreadaEnUtc);

public class ObtenerDeteccionesPorEmpresaQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDeteccionesPorEmpresaQuery, Result<IReadOnlyList<DeteccionTrabajadorDto>>>
{
    public async Task<Result<IReadOnlyList<DeteccionTrabajadorDto>>> Handle(
        ObtenerDeteccionesPorEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return Result.Fallo<IReadOnlyList<DeteccionTrabajadorDto>>(Error.Crear(
                "Empresa.NoEncontrada", "No encontramos esta empresa."));

        var detecciones = await dbContext.DeteccionesTrabajador
            .Where(d => d.EmpresaId == request.EmpresaId && !d.Resuelta)
            .OrderBy(d => d.Tipo).ThenBy(d => d.Apellidos)
            .Select(d => new DeteccionTrabajadorDto(d.Id, d.Tipo, d.Nombre, d.Apellidos, d.Dni, d.TrabajadorExistenteId, d.CreadaEnUtc))
            .ToListAsync(cancellationToken);

        return Result.Exito<IReadOnlyList<DeteccionTrabajadorDto>>(detecciones);
    }
}
