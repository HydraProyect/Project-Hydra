using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarImportacionExcelQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionDto>;

public class AnalizarImportacionExcelQueryHandler(IExcelImportacionParser parser)
    : IRequestHandler<AnalizarImportacionExcelQuery, PlanImportacionDto>
{
    public async Task<PlanImportacionDto> Handle(AnalizarImportacionExcelQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await parser.AnalizarAsync(stream, cancellationToken);
    }
}
