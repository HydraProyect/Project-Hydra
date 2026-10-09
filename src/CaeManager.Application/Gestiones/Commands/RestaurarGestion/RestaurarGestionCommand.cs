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

        // Misma autoridad que la baja: el Centro de la Gestión. No se comprueba que el Centro y el
        // Trabajador sigan vivos: con alcance acotado un Centro eliminado ya no está entre los
        // visibles y la restauración se rechaza, pero con alcance de Tenant entero la Gestión vuelve
        // aunque su Centro siga eliminado — igual que RestaurarTrabajadorCommand con su Empresa.
        if (gestion is null || !gestion.EstaEliminado
            || !await alcanceDatos.CentroVisibleAsync(gestion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("Gestion.NoEncontrada", "No encontramos esta gestión eliminada."));

        gestion.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
