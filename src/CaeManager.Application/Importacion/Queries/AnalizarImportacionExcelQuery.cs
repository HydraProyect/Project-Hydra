using MediatR;

namespace CaeManager.Application.Importacion.Queries;

public record AnalizarImportacionExcelQuery(byte[] ContenidoArchivo) : IRequest<PlanImportacionDto>, Common.ILecturaRegistradaEnSesionPrivilegiada;

public class AnalizarImportacionExcelQueryHandler(IExcelImportacionParser parser, RecorteDeAnalisisAlAmbito recorte)
    : IRequestHandler<AnalizarImportacionExcelQuery, PlanImportacionDto>
{
    public async Task<PlanImportacionDto> Handle(AnalizarImportacionExcelQuery request, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(request.ContenidoArchivo);
        return await recorte.RecortarAsync(await parser.AnalizarAsync(stream, cancellationToken), cancellationToken);
    }
}
