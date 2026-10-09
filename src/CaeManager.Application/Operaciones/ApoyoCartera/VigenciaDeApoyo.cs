using CaeManager.Domain.Common;

namespace CaeManager.Application.Operaciones.ApoyoCartera;

/// <summary>
/// La fecha de fin opcional de un apoyo de cartera es un <b>día de negocio</b>
/// (<see cref="DiaDeNegocio"/>, hora peninsular): «apoyo hasta el día D» incluye el día D
/// entero. La Asignación de Cartera guarda un instante UTC en el que deja de estar vigente, así
/// que la conversión entre el día que se elige y se pinta y el instante que se guarda vive aquí,
/// en un solo sitio.
/// </summary>
public static class VigenciaDeApoyo
{
    /// <summary>El instante UTC en que deja de estar vigente un apoyo cuyo último día es <paramref name="ultimoDia"/>.</summary>
    public static DateTime HastaElFinalDe(DateOnly ultimoDia) => DiaDeNegocio.InicioEnUtc(ultimoDia.AddDays(1));

    /// <summary>El último día de negocio en que el apoyo sigue vigente: el que se pinta en «Apoyo hasta».</summary>
    public static DateOnly UltimoDia(DateTime vigenciaHastaUtc) => DiaDeNegocio.De(vigenciaHastaUtc.AddTicks(-1));
}
