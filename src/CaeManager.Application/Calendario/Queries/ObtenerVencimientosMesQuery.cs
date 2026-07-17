using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Calendario.Queries;

/// <summary>
/// Vencimientos de Documento dentro de un mes calendario, para la vista
/// mensual de Calendario (ver ROADMAP.md, Fase 3). Mismo cálculo de estado
/// que Dashboard/Alertas — nunca puede haber dos semáforos distintos para
/// el mismo documento.
/// </summary>
public record ObtenerVencimientosMesQuery(int Anio, int Mes) : IRequest<IReadOnlyList<VencimientoCalendarioDto>>;

public record VencimientoCalendarioDto(
    Guid DocumentoId,
    DateOnly FechaVencimiento,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    EstadoDocumento Estado);

public class ObtenerVencimientosMesQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerVencimientosMesQuery, IReadOnlyList<VencimientoCalendarioDto>>
{
    public async Task<IReadOnlyList<VencimientoCalendarioDto>> Handle(ObtenerVencimientosMesQuery request, CancellationToken cancellationToken)
    {
        var primerDia = new DateOnly(request.Anio, request.Mes, 1);
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);

        var parametros = await dbContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var filas = await (
            from documento in dbContext.Documentos
            join trabajador in dbContext.Trabajadores on documento.TrabajadorId equals trabajador.Id
            join tipoDocumento in dbContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where documento.FechaVencimiento != null
                && documento.FechaVencimiento >= primerDia
                && documento.FechaVencimiento <= ultimoDia
            select new
            {
                documento.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                tipoDocumento.Nombre,
                documento.FechaVencimiento
            })
            .ToListAsync(cancellationToken);

        return filas
            .Select(f => new VencimientoCalendarioDto(
                f.Id,
                f.FechaVencimiento!.Value,
                f.TrabajadorNombre,
                f.Nombre,
                CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)))
            .OrderBy(v => v.FechaVencimiento)
            .ToList();
    }
}
