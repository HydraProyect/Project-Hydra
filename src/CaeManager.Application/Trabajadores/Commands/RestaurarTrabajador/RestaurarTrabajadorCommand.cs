using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarTrabajadorCommand(Guid Id) : ICommand;

public class RestaurarTrabajadorCommandHandler(ITrabajadoresQueryContext trabajadoresContext, ITenantActual tenantActual, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarTrabajadorCommand, Result>
{
    public async Task<Result> Handle(RestaurarTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.Id && t.TenantId == tenantActual.TenantId, cancellationToken);

        if (trabajador is null || !trabajador.EstaEliminado)
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador eliminado."));

        trabajador.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
