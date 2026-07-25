using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>Cuántas verificaciones IA de Documento se resolvieron solas (Automática) vs necesitaron un Gestor CAE (Manual) — ver AprobacionDocumento.</summary>
public record ObtenerEstadisticasAprobacionDocumentoQuery : IRequest<EstadisticasAprobacionDocumentoDto>;

public record EstadisticasAprobacionDocumentoDto(int Automaticas, int Manuales);

public class ObtenerEstadisticasAprobacionDocumentoQueryHandler(IApplicationDbContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerEstadisticasAprobacionDocumentoQuery, EstadisticasAprobacionDocumentoDto>
{
    public async Task<EstadisticasAprobacionDocumentoDto> Handle(
        ObtenerEstadisticasAprobacionDocumentoQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var consulta =
            from aprobacion in dbContext.AprobacionesDocumento
            join documento in dbContext.Documentos on aprobacion.DocumentoId equals documento.Id
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            select aprobacion.Tipo;

        var tipos = await consulta.ToListAsync(cancellationToken);

        return new EstadisticasAprobacionDocumentoDto(
            Automaticas: tipos.Count(t => t == TipoAprobacionDocumento.Automatica),
            Manuales: tipos.Count(t => t == TipoAprobacionDocumento.Manual));
    }
}
