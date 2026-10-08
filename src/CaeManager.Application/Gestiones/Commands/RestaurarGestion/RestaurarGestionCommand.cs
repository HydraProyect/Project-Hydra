using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones.Commands.RestaurarGestion;

/// <summary>
/// «Deshacer» de <c>EliminarGestionCommand</c> — ver RestaurarClienteCommand para el razonamiento
/// completo del IgnoreQueryFilters()+TenantId. La baja solo marca la Gestión, así que restaurarla la
/// devuelve con el estado, las fechas y el responsable que tenía.
/// </summary>
public record RestaurarGestionCommand(Guid Id) : ICommand;

public class RestaurarGestionCommandHandler(
    IGestionesQueryContext gestionesContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarGestionCommand, Result>
{
    public async Task<Result> Handle(RestaurarGestionCommand request, CancellationToken cancellationToken)
    {
        var gestion = await gestionesContext.Gestiones
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(g => g.Id == request.Id && g.TenantId == tenantActual.TenantId, cancellationToken);

        // Misma autoridad que la baja: el Centro de la Gestión, que sigue vivo y por tanto en la lista
        // de visibles. Si el Centro se eliminó entretanto, la Gestión no se restaura por este camino.
        if (gestion is null || !gestion.EstaEliminado
            || !await alcanceDatos.CentroVisibleAsync(gestion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("Gestion.NoEncontrada", "No encontramos esta gestión eliminada."));

        gestion.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
