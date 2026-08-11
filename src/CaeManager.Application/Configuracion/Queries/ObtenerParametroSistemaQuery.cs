using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Configuracion.Queries;

public record ObtenerParametroSistemaQuery : IRequest<ParametroSistemaDto>;

public record ParametroSistemaDto(
    int UmbralAmbarDias,
    int UmbralRojoDias,
    TimeOnly HoraInicioJornada,
    TimeOnly HoraFinJornada,
    int HorasJornadaMensualGestor,
    bool MedicionTiempoActiva,
    int SegundosInactividadPausa,
    bool ExcluirFueraDeJornadaEnMetricas);

public class ObtenerParametroSistemaQueryHandler(IConfiguracionQueryContext dbContext)
    : IRequestHandler<ObtenerParametroSistemaQuery, ParametroSistemaDto>
{
    public async Task<ParametroSistemaDto> Handle(ObtenerParametroSistemaQuery request, CancellationToken cancellationToken)
    {
        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        return new ParametroSistemaDto(
            parametros.UmbralAmbarDias,
            parametros.UmbralRojoDias,
            parametros.HoraInicioJornada,
            parametros.HoraFinJornada,
            parametros.HorasJornadaMensualGestor,
            parametros.MedicionTiempoActiva,
            parametros.SegundosInactividadPausa,
            parametros.ExcluirFueraDeJornadaEnMetricas);
    }
}
