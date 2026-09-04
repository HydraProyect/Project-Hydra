using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerDeteccionesPorEmpresa;

/// <summary>Detecciones pendientes (sin resolver) de altas/bajas de personal para una Empresa — ver DeteccionTrabajadoresService.</summary>
public record ObtenerDeteccionesPorEmpresaQuery(Guid EmpresaId) : IRequest<Result<IReadOnlyList<DeteccionTrabajadorDto>>>;

public record DeteccionTrabajadorDto(
    Guid Id, TipoDeteccion Tipo, string Nombre, string Apellidos, string Dni, Guid? TrabajadorExistenteId, DateTime CreadaEnUtc);

public class ObtenerDeteccionesPorEmpresaQueryHandler(ITrabajadoresQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDeteccionesPorEmpresaQuery, Result<IReadOnlyList<DeteccionTrabajadorDto>>>
{
    public async Task<Result<IReadOnlyList<DeteccionTrabajadorDto>>> Handle(
        ObtenerDeteccionesPorEmpresaQuery request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN, no de lectura (REC-149): las detecciones son una
        // herramienta de conciliación interna de personal (altas/bajas de la
        // Empresa entera, sin relación con un Cliente concreto) que alimenta
        // ResolverDeteccionAusenteCommand/ResolverDeteccionNuevoCommand, e
        // incluyen el DNI de cada trabajador detectado. No es documentación
        // de cumplimiento en la relación con el propio Cliente — es
        // información operativa de personal de la contratista, que un
        // usuario de portal (rol Cliente) no debería poder consultar solo
        // por tener a esa Empresa en su cartera de lectura.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(request.EmpresaId, cancellationToken))
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
