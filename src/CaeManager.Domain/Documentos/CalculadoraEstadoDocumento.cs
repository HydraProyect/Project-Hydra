namespace CaeManager.Domain.Documentos;

/// <summary>
/// Calcula el estado de vigencia de un Documento. Es el corazón del producto:
/// alimenta los semáforos de tabla, los KPIs del Dashboard y las Alertas.
/// Lógica pura, sin dependencias — igual que las fórmulas del Excel original,
/// pero centralizada y con umbrales configurables (ver DATABASE.md).
/// </summary>
public static class CalculadoraEstadoDocumento
{
    /// <summary>
    /// Ya no recibe "aplicaVencimientoAutomatico": FechaVencimiento por sí
    /// sola basta para saber si el documento tiene vigencia que vigilar —
    /// para un TipoDocumento sin vencimiento automático puede venir de una
    /// fecha introducida a mano (ver Fase 13), y participa en el semáforo
    /// igual que una calculada. Nula = el documento no tiene vigencia.
    /// </summary>
    public static EstadoDocumento Calcular(
        DateOnly? fechaVencimiento,
        DateOnly hoy,
        int umbralAmbarDias,
        int umbralRojoDias)
    {
        if (fechaVencimiento is null)
            return EstadoDocumento.SinCaducidad;

        var diasRestantes = fechaVencimiento.Value.DayNumber - hoy.DayNumber;

        if (diasRestantes < 0)
            return EstadoDocumento.Vencido;

        if (diasRestantes <= umbralRojoDias)
            return EstadoDocumento.Urgente;

        if (diasRestantes <= umbralAmbarDias)
            return EstadoDocumento.Proximo;

        return EstadoDocumento.Vigente;
    }

    /// <summary>
    /// Calcula la fecha de vencimiento de un documento a partir de su fecha de
    /// emisión y la vigencia en meses del TipoDocumento (o de la periodicidad
    /// especial de un RequisitoDocumental, si el llamador la pasa en su lugar).
    /// </summary>
    public static DateOnly? CalcularFechaVencimiento(DateOnly fechaEmision, int? vigenciaMeses) =>
        vigenciaMeses.HasValue ? fechaEmision.AddMonths(vigenciaMeses.Value) : null;
}
