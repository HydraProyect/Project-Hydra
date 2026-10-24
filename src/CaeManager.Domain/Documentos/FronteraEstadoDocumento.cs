namespace CaeManager.Domain.Documentos;

/// <summary>
/// El primer día en que el estado de un documento cambia solo por el paso del tiempo: la frontera que guarda el estado
/// materializado en <c>ProximoCambioEl</c> para que la pasada de medianoche toque solo lo que cambia ese día.
///
/// <para>
/// Es la misma regla que <see cref="CalculadoraEstadoDocumento.Calcular(VigenciaDocumento, DateOnly, int, int)"/>, leída al
/// revés: el estado depende de <c>FechaVencimiento − hoy</c> comparado con los umbrales, así que solo puede cambiar el día
/// en que entra en el umbral ámbar (<c>FV − UmbralAmbarDias</c>), en el rojo (<c>FV − UmbralRojoDias</c>) o en que vence
/// (<c>FV + 1</c>). El día de negocio es el de Madrid (<see cref="Common.DiaDeNegocio"/>): un documento nunca cambia a
/// mitad del día (decisión del 2026-10-24).
/// </para>
///
/// <para>
/// <b>Contrato: nunca tarde.</b> Antes de la frontera el estado no cambia; en la frontera puede cambiar o no (con los dos
/// umbrales iguales, el día ámbar y el rojo coinciden). Una frontera temprana solo cuesta una fila recalculada de más; una
/// tardía dejaría un estado viejo, que es justo lo que la decisión prohíbe. Lo fija
/// <c>FronteraEstadoDocumentoTests</c> contra la calculadora, día a día.
/// </para>
/// </summary>
public static class FronteraEstadoDocumento
{
    /// <summary>
    /// Primer día posterior a <paramref name="hoy"/> en que el estado de <paramref name="vigencia"/> puede cambiar por la
    /// fecha; <c>null</c> si nunca (no caduca, o sin confirmar: esos solo cambian cuando alguien edita el documento).
    /// </summary>
    public static DateOnly? ProximoCambio(VigenciaDocumento vigencia, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias) =>
        vigencia.Estado == EstadoVigenciaDocumento.NoCaduca || vigencia.FechaVencimiento is not { } fechaVencimiento
            ? null
            : PrimeraPosterior(
                hoy,
                Desplazar(fechaVencimiento, -umbralAmbarDias),
                Desplazar(fechaVencimiento, -umbralRojoDias),
                DiaSiguiente(fechaVencimiento));

    /// <inheritdoc cref="ProximoCambio(VigenciaDocumento, DateOnly, int, int)"/>
    public static DateOnly? ProximoCambio(
        EstadoVigenciaDocumento estadoVigencia, DateOnly? fechaVencimiento, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias) =>
        ProximoCambio(VigenciaDocumento.Rehidratar(estadoVigencia, fechaVencimiento), hoy, umbralAmbarDias, umbralRojoDias);

    /// <summary>La fecha más temprana de las candidatas que sea posterior a <paramref name="hoy"/>; <c>null</c> si ninguna.</summary>
    public static DateOnly? PrimeraPosterior(DateOnly hoy, params DateOnly?[] candidatas)
    {
        DateOnly? primera = null;
        foreach (var candidata in candidatas)
        {
            if (candidata is { } fecha && fecha > hoy && (primera is null || fecha < primera))
                primera = fecha;
        }

        return primera;
    }

    /// <summary>
    /// El día siguiente a <paramref name="fecha"/>; <c>null</c> si no hay (es <see cref="DateOnly.MaxValue"/>, el vencimiento
    /// centinela 9999-12-31 o una periodicidad saturada): ese cambio no llega nunca, así que no es frontera. Nunca lanza, para
    /// que una sola fila rara no tumbe el relleno ni el recálculo de una acción.
    /// </summary>
    public static DateOnly? DiaSiguiente(DateOnly? fecha) => fecha is { } f ? Desplazar(f, 1) : null;

    /// <summary><paramref name="fecha"/> más <paramref name="dias"/> (que pueden ser negativos); <c>null</c> fuera del rango de <see cref="DateOnly"/>.</summary>
    private static DateOnly? Desplazar(DateOnly fecha, int dias)
    {
        var dia = (long)fecha.DayNumber + dias;
        return dia < DateOnly.MinValue.DayNumber || dia > DateOnly.MaxValue.DayNumber ? null : DateOnly.FromDayNumber((int)dia);
    }
    /// <summary>La más temprana de dos fronteras ya calculadas; <c>null</c> solo si las dos lo son.</summary>
    public static DateOnly? Minima(DateOnly? una, DateOnly? otra) =>
        una is null ? otra : otra is null ? una : (una < otra ? una : otra);
}
