using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;

public record ObtenerProyectoPorIdQuery(Guid Id) : IRequest<ProyectoDetalleDto?>;

public record ProyectoDetalleDto(
    Guid Id,
    Guid ClienteId,
    string ClienteRazonSocial,
    Guid CentroId,
    string CentroNombre,
    string Nombre,
    DateOnly FechaInicio,
    DateOnly? FechaFinPrevista,
    DateOnly? FechaCierreReal,
    bool EstaAbierto,
    string? Notas,
    int TecnicosActivos,
    int DocumentosGestionados,
    Guid Version);

public class ObtenerProyectoPorIdQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerProyectoPorIdQuery, ProyectoDetalleDto?>
{
    public async Task<ProyectoDetalleDto?> Handle(ObtenerProyectoPorIdQuery request, CancellationToken cancellationToken)
    {
        var proyecto = await dbContext.Proyectos.FirstOrDefaultAsync(p => p.Id == request.Id, cancellationToken);
        if (proyecto is null) return null;

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null && !clienteIdsVisibles.Contains(proyecto.ClienteId))
            return null;

        var clienteRazonSocial = await dbContext.Clientes
            .Where(c => c.Id == proyecto.ClienteId).Select(c => c.RazonSocial).FirstAsync(cancellationToken);
        var centroNombre = await dbContext.Centros
            .Where(c => c.Id == proyecto.CentroId).Select(c => c.Nombre).FirstAsync(cancellationToken);

        var tecnicosActivos = await dbContext.ProyectosTecnicos
            .CountAsync(pt => pt.ProyectoId == proyecto.Id && pt.FechaBaja == null, cancellationToken);
        var documentosGestionados = await dbContext.Documentos
            .CountAsync(d => d.ProyectoId == proyecto.Id, cancellationToken);

        return new ProyectoDetalleDto(
            proyecto.Id, proyecto.ClienteId, clienteRazonSocial, proyecto.CentroId, centroNombre, proyecto.Nombre,
            proyecto.FechaInicio, proyecto.FechaFinPrevista, proyecto.FechaCierreReal, proyecto.FechaCierreReal == null,
            proyecto.Notas, tecnicosActivos, documentosGestionados, proyecto.Version);
    }
}
