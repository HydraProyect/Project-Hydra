using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reportes.Queries.ObtenerHistorialInformes;

public record ObtenerHistorialInformesQuery(int Limite = 10) : IRequest<IReadOnlyList<HistorialInformeDto>>;

public record HistorialInformeDto(Guid Id, string TipoInforme, string? ClienteNombre, DateTime GeneradoEnUtc, Guid GeneradoPorUsuarioId);

public class ObtenerHistorialInformesQueryHandler(IReportesQueryContext reportesContext)
    : IRequestHandler<ObtenerHistorialInformesQuery, IReadOnlyList<HistorialInformeDto>>
{
    public async Task<IReadOnlyList<HistorialInformeDto>> Handle(ObtenerHistorialInformesQuery request, CancellationToken cancellationToken) =>
        await reportesContext.HistorialInformes
            .OrderByDescending(h => h.GeneradoEnUtc)
            .Take(request.Limite)
            .Select(h => new HistorialInformeDto(h.Id, h.TipoInforme, h.ClienteNombre, h.GeneradoEnUtc, h.GeneradoPorUsuarioId))
            .ToListAsync(cancellationToken);
}
