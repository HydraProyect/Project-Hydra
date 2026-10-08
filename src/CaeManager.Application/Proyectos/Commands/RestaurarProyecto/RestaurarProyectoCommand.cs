using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Proyectos.Commands.RestaurarProyecto;

/// <summary>
/// «Deshacer» de <c>EliminarProyectoCommand</c> — ver RestaurarClienteCommand para el razonamiento
/// completo del IgnoreQueryFilters()+TenantId. La baja solo marca el Proyecto: sus técnicos, visitas
/// y trabajadores asociados no se tocan, así que restaurarlo lo devuelve entero.
/// </summary>
public record RestaurarProyectoCommand(Guid Id) : ICommand;

public class RestaurarProyectoCommandHandler(
    IProyectosQueryContext proyectosContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarProyectoCommand, Result>
{
    public async Task<Result> Handle(RestaurarProyectoCommand request, CancellationToken cancellationToken)
    {
        var proyecto = await proyectosContext.Proyectos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == request.Id && p.TenantId == tenantActual.TenantId, cancellationToken);

        // Misma autoridad que la baja (ProyectoAutorizacion): va por el ClienteId persistido, que no
        // depende de la fila que se está restaurando.
        if (proyecto is null || !proyecto.EstaEliminado
            || !await ProyectoAutorizacion.VisibleAsync(proyecto.ClienteId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("Proyecto.NoEncontrado", "No encontramos este proyecto eliminado."));

        proyecto.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
