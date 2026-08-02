using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Calendario.Queries;

/// <summary>
/// Vencimientos de Documento dentro de un mes calendario, para la vista
/// mensual de Calendario (ver ROADMAP.md, Fase 3). Mismo cálculo de estado
/// que Dashboard/Alertas — nunca puede haber dos semáforos distintos para
/// el mismo documento. Solo cubre Documentos de Trabajador — los de
/// Cliente/Empresa no aparecen en el calendario todavía (fuera de alcance).
/// </summary>
public record ObtenerVencimientosMesQuery(int Anio, int Mes) : IRequest<IReadOnlyList<VencimientoCalendarioDto>>;

public record VencimientoCalendarioDto(
    Guid DocumentoId,
    DateOnly FechaVencimiento,
    string TrabajadorNombre,
    string TipoDocumentoNombre,
    EstadoDocumento Estado);

public class ObtenerVencimientosMesQueryHandler(IConfiguracionQueryContext configuracionContext, IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVencimientosMesQuery, IReadOnlyList<VencimientoCalendarioDto>>
{
    public async Task<IReadOnlyList<VencimientoCalendarioDto>> Handle(ObtenerVencimientosMesQuery request, CancellationToken cancellationToken)
    {
        var primerDia = new DateOnly(request.Anio, request.Mes, 1);
        var ultimoDia = primerDia.AddMonths(1).AddDays(-1);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var filas = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
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
