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
                fechaVencimiento.AddDays(-umbralAmbarDias),
                fechaVencimiento.AddDays(-umbralRojoDias),
                fechaVencimiento.AddDays(1));

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

    /// <summary>La más temprana de dos fronteras ya calculadas; <c>null</c> solo si las dos lo son.</summary>
    public static DateOnly? Minima(DateOnly? una, DateOnly? otra) =>
        una is null ? otra : otra is null ? una : (una < otra ? una : otra);
}
