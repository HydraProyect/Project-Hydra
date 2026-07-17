using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarPlantillaCombinadaQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionCombinadaDto>;

public class AnalizarPlantillaCombinadaQueryHandler(IPlantillaCombinadaService servicio)
    : IRequestHandler<AnalizarPlantillaCombinadaQuery, PlanImportacionCombinadaDto>
{
    public async Task<PlanImportacionCombinadaDto> Handle(AnalizarPlantillaCombinadaQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, cancellationToken);
    }
}
