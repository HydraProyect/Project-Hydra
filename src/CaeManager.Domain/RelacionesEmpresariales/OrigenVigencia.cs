namespace CaeManager.Domain.RelacionesEmpresariales;

/// <summary>
/// De dónde sale <see cref="RelacionEmpresarial.VigenciaDesde"/> — ADR-011
/// § 17: ninguna migración puede convertir "fecha desconocida" en "fecha
/// supuesta" solo para satisfacer el NOT NULL. <see cref="InferidaPorMigracion"/>
/// marca que la fecha es una cota de referencia del sistema (la fecha de
/// alta conocida de la contraparte), nunca un hecho contractual — ningún
/// consumidor legal/de cumplimiento puede tratarla como confirmada.
/// </summary>
public enum OrigenVigencia
{
    HistoricaConfirmada,
    InferidaPorMigracion,
}
