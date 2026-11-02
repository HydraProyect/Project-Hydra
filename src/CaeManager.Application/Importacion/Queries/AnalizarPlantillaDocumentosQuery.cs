using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarPlantillaDocumentosQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionDto>;

public class AnalizarPlantillaDocumentosQueryHandler(IPlantillaDocumentosService servicio, RecorteDeAnalisisAlAmbito recorte)
    : IRequestHandler<AnalizarPlantillaDocumentosQuery, PlanImportacionDto>
{
    public async Task<PlanImportacionDto> Handle(AnalizarPlantillaDocumentosQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await recorte.RecortarAsync(await servicio.AnalizarAsync(stream, cancellationToken), cancellationToken);
    }
}
