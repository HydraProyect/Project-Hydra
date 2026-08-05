using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.RestaurarCentro;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarCentroCommand(Guid Id) : ICommand;

public class RestaurarCentroCommandHandler(ICentrosQueryContext centrosContext, ITenantActual tenantActual, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarCentroCommand, Result>
{
    public async Task<Result> Handle(RestaurarCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await centrosContext.Centros
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tenantActual.TenantId, cancellationToken);

        if (centro is null || !centro.EstaEliminado)
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro eliminado."));

        centro.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
