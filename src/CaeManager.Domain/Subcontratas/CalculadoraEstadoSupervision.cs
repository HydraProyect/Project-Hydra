namespace CaeManager.Domain.Subcontratas;

/// <summary>
/// Calcula el estado de supervisión de un tipo documental exigido a una
/// Subcontrata en un Centro, a partir de su última verificación externa no
/// eliminada (ADR-005 § 2.3). Lógica pura y umbrales de ParametroSistema —
/// mismo patrón que <c>CalculadoraEstadoDocumento</c>, que es la regla
/// central del producto.
/// </summary>
public static class CalculadoraEstadoSupervision
{
    public static EstadoSupervision Calcular(
        ResultadoVerificacionExterna? resultadoUltimaVerificacion,
        DateOnly? validoHasta,
        DateOnly hoy,
        int umbralAmbarDias,
        int umbralRojoDias)
    {
        if (resultadoUltimaVerificacion is null)
            return EstadoSupervision.SinVerificar;

        if (resultadoUltimaVerificacion is not ResultadoVerificacionExterna.Valido)
            return EstadoSupervision.NoValido;

        // Válido sin caducidad declarada: la plataforma no informó fecha —
        // vigente hasta que una verificación posterior diga otra cosa.
        if (validoHasta is null)
            return EstadoSupervision.Vigente;

        var diasRestantes = validoHasta.Value.DayNumber - hoy.DayNumber;

        if (diasRestantes < 0)
            return EstadoSupervision.Vencido;

        if (diasRestantes <= umbralRojoDias)
            return EstadoSupervision.Urgente;

        if (diasRestantes <= umbralAmbarDias)
            return EstadoSupervision.Proximo;

        return EstadoSupervision.Vigente;
    }
}
