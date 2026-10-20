using CaeManager.Domain.Visitas;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// La asignación de un Trabajador a una Visita sembrada, con una fecha de solicitud coherente con la Visita: dos días
/// antes de su inicio, a las 9:00 UTC. Sin ella, la exportación de Trabajadores extranjeros del mes de una demo
/// pondría como fecha de solicitud el instante de la siembra, posterior a Visitas ya terminadas.
/// </summary>
internal static class AsignacionSembrada
{
    public static VisitaTrabajador De(Visita visita, Guid trabajadorId) =>
        new(visita.Id, trabajadorId,
            fechaAsignacionUtc: DateTime.SpecifyKind(
                visita.FechaInicio.AddDays(-2).ToDateTime(new TimeOnly(9, 0)), DateTimeKind.Utc));
}
