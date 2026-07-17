using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarPlantillaDocumentosQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionDto>;

public class AnalizarPlantillaDocumentosQueryHandler(IPlantillaDocumentosService servicio)
    : IRequestHandler<AnalizarPlantillaDocumentosQuery, PlanImportacionDto>
{
    public async Task<PlanImportacionDto> Handle(AnalizarPlantillaDocumentosQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
