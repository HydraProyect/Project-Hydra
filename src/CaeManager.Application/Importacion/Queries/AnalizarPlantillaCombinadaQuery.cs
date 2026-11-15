using CaeManager.Application.Tenants.Encargo;
using MediatR;

namespace CaeManager.Application.Importacion.Queries;

[AreaDelEncargo("Importacion")]
public record AnalizarPlantillaCombinadaQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionCombinadaDto>, Common.ILecturaRegistradaEnSesionPrivilegiada;

public class AnalizarPlantillaCombinadaQueryHandler(IPlantillaCombinadaService servicio, RecorteDeAnalisisAlAmbito recorte)
    : IRequestHandler<AnalizarPlantillaCombinadaQuery, PlanImportacionCombinadaDto>
{
    public async Task<PlanImportacionCombinadaDto> Handle(AnalizarPlantillaCombinadaQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await servicio.AnalizarAsync(stream, await recorte.ObtenerExistentesAsync(cancellationToken), cancellationToken);
    }
}
