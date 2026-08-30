using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.RestaurarCentro;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarCentroCommand(Guid Id) : ICommand;

public class RestaurarCentroCommandHandler(
    ICentrosQueryContext centrosContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarCentroCommand, Result>
{
    public async Task<Result> Handle(RestaurarCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await centrosContext.Centros
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tenantActual.TenantId, cancellationToken);

        if (centro is null || !centro.EstaEliminado)
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro eliminado."));

        // Autoridad por el cliente, no solo tenant (auditoría Módulo 5,
        // hallazgo crítico 8/9): CentroVisibleAsync no sirve aquí porque su
        // consulta pasa por el filtro global de soft delete, que excluye
        // justamente la fila que se está restaurando — el ClienteId
        // persistido es la coordenada estable.
        if (!await alcanceDatos.ClienteVisibleAsync(centro.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro eliminado."));

        centro.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
