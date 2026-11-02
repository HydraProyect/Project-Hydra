using CaeManager.Application.Importacion.Maestra;
using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarPlantillaMaestraQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionMaestraDto>;

public class AnalizarPlantillaMaestraQueryHandler(IPlantillaMaestraService servicio)
    : IRequestHandler<AnalizarPlantillaMaestraQuery, PlanImportacionMaestraDto>
{
    public async Task<PlanImportacionMaestraDto> Handle(AnalizarPlantillaMaestraQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
