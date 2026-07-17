using CaeManager.Application.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

public record ObtenerParametroSistemaQuery : IRequest<ParametroSistemaDto>;

public record ParametroSistemaDto(int UmbralAmbarDias, int UmbralRojoDias);

public class ObtenerParametroSistemaQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerParametroSistemaQuery, ParametroSistemaDto>
{
    public async Task<ParametroSistemaDto> Handle(ObtenerParametroSistemaQuery request, CancellationToken cancellationToken)
    {
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        return new ParametroSistemaDto(parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
    }
}
