namespace CaeManager.Domain.Visitas;

/// <summary>
/// Calcula el nivel de urgencia de una Visita respecto a la ventana mínima
/// de validación de la plataforma del cliente (Fase F). Lógica pura, sin
/// dependencias — mismo espíritu que CalculadoraEstadoDocumento.
///
/// <see cref="Visita.FechaInicio"/> es un <see cref="DateOnly"/> (sin hora),
/// así que la ventana de horas se traduce a días naturales completos: "menos
/// de 24h" se aproxima a "la visita es hoy", "24-48h" a "la visita es
/// mañana". Es una aproximación deliberada — Visita nunca ha registrado hora
/// de entrada, y añadirla sería un cambio de modelo mayor, fuera de alcance
/// de esta fase (YAGNI: la ventana de 24/48h del pedido original ya es
/// aproximada en el propio Excel/plataformas de origen).
/// </summary>
public static class CalculadoraUrgenciaVisita
{
    public static NivelUrgenciaVisita Calcular(
        DateOnly fechaInicio,
        DateOnly fechaFin,
        DateOnly hoy,
        int horasAvisoVisita,
        int horasCriticasVisita)
    {
        if (fechaInicio <= hoy && hoy <= fechaFin)
            return NivelUrgenciaVisita.EnCurso;

        if (fechaInicio < hoy)
            return NivelUrgenciaVisita.Normal;

        var horasHastaInicio = (fechaInicio.DayNumber - hoy.DayNumber) * 24;

        if (horasHastaInicio <= horasCriticasVisita)
            return NivelUrgenciaVisita.Critica;

        if (horasHastaInicio <= horasAvisoVisita)
            return NivelUrgenciaVisita.Urgente;

        return NivelUrgenciaVisita.Normal;
    }
}
