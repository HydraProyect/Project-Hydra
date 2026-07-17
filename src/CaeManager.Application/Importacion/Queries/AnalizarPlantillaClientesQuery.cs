using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarPlantillaClientesQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionDto>;

public class AnalizarPlantillaClientesQueryHandler(IPlantillaClientesService servicio)
    : IRequestHandler<AnalizarPlantillaClientesQuery, PlanImportacionDto>
{
    public async Task<PlanImportacionDto> Handle(AnalizarPlantillaClientesQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
