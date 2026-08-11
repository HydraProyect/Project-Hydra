using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Pulso semanal del equipo (DDL-071): cuántas verificaciones de Documento
/// se resolvieron esta semana (automáticas + manuales, ver
/// AprobacionDocumento), comparadas con la semana anterior y con la mejor
/// semana histórica. Competición contra el propio histórico, nunca entre
/// personas: el agregado no se desglosa por usuario a propósito. El alcance
/// es el mismo que el resto del Dashboard (IAlcanceDatosService) — "equipo"
/// significa lo que el usuario supervisa, no el tenant entero saltándose la
/// visibilidad por cartera.
/// </summary>
public record ObtenerPulsoEquipoQuery : IRequest<PulsoEquipoDto>;

public record PulsoEquipoDto(
    int ResueltosEstaSemana,
    int ResueltosSemanaAnterior,
    int MejorSemanaHistorica,
    DateOnly? MejorSemanaInicio,
    bool EsMejorSemana);

public class ObtenerPulsoEquipoQueryHandler(IDocumentosQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPulsoEquipoQuery, PulsoEquipoDto>
{
    public async Task<PulsoEquipoDto> Handle(ObtenerPulsoEquipoQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var fechas = await (
            from aprobacion in dbContext.AprobacionesDocumento
            join documento in dbContext.Documentos on aprobacion.DocumentoId equals documento.Id
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            select aprobacion.CreadaEnUtc)
            .ToListAsync(cancellationToken);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var inicioSemanaActual = InicioSemana(hoy);

        var porSemana = fechas
            .GroupBy(f => InicioSemana(DateOnly.FromDateTime(f)))
            .ToDictionary(g => g.Key, g => g.Count());

        var estaSemana = porSemana.GetValueOrDefault(inicioSemanaActual);
        var semanaAnterior = porSemana.GetValueOrDefault(inicioSemanaActual.AddDays(-7));

        // La mejor semana se busca solo entre semanas ya cerradas: comparar la
        // semana en curso (parcial) consigo misma la convertiría en "mejor
        // semana" cada lunes con un solo documento resuelto.
        var semanasCerradas = porSemana.Where(s => s.Key < inicioSemanaActual).ToList();
        var mejor = semanasCerradas.OrderByDescending(s => s.Value).ThenByDescending(s => s.Key).FirstOrDefault();

        return new PulsoEquipoDto(
            ResueltosEstaSemana: estaSemana,
            ResueltosSemanaAnterior: semanaAnterior,
            MejorSemanaHistorica: mejor.Value,
            MejorSemanaInicio: semanasCerradas.Count > 0 ? mejor.Key : (DateOnly?)null,
            EsMejorSemana: estaSemana > 0 && estaSemana > mejor.Value);
    }

    /// <summary>Lunes de la semana a la que pertenece la fecha.</summary>
    private static DateOnly InicioSemana(DateOnly fecha) => fecha.AddDays(-(((int)fecha.DayOfWeek + 6) % 7));
}
