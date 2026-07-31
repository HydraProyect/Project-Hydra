namespace CaeManager.Domain.Documentos;

/// <summary>
/// Desde qué evento empieza a contar el plazo de retención.
/// </summary>
public enum CriterioInicioRetencion
{
    /// <summary>
    /// El evento que ocurre <b>antes</b> (predeterminado, decidido por el
    /// propietario del producto). Un documento tiene vida propia como
    /// evidencia: desde que deja de estar vigente hay que conservarlo su
    /// plazo, independientemente de que la relación siga abierta. Además
    /// hace la fecha de purga previsible — dos documentos con la misma
    /// vigencia se purgan a la vez, sin depender de cuándo se subieron.
    /// </summary>
    EventoMasTemprano,

    /// <summary>
    /// El evento que ocurre <b>después</b>. Más conservador de cara a la
    /// prescripción de responsabilidades en el orden social: no purga
    /// mientras la relación siga viva. A cambio, un documento puede
    /// conservarse mucho más allá de su vigencia.
    /// </summary>
    EventoMasTardio
}

/// <summary>
/// Cuándo puede purgarse un Documento. Lógica pura y sin dependencias, igual
/// que <see cref="CalculadoraEstadoDocumento"/> — el plazo y el criterio
/// llegan como parámetros para que se configuren fuera y no haya ninguna
/// política de retención escrita a fuego en el código.
///
/// No decide <b>qué</b> significa purgar (borrado físico o anonimización) ni
/// <b>cuándo</b> se ejecuta: solo la fecha a partir de la cual un documento
/// deja de tener que conservarse.
/// </summary>
public static class CalculadoraRetencionDocumento
{
    /// <summary>
    /// El evento propio del documento: su fin de vigencia, o su emisión si no
    /// vence nunca.
    ///
    /// Usar la emisión para los documentos sin vencimiento es lo que evita
    /// tener documentos perpetuos: sin este caso, un documento sin fecha de
    /// caducidad no tendría nunca un evento del que partir y se conservaría
    /// para siempre.
    /// </summary>
    public static DateOnly EventoDelDocumento(DateOnly fechaEmision, DateOnly? fechaVencimiento) =>
        fechaVencimiento ?? fechaEmision;

    /// <summary>
    /// Fecha en la que arranca el plazo de retención.
    ///
    /// <paramref name="fechaCeseRelacion"/> es la baja del trabajador, el
    /// cierre del proyecto o el fin de la relación con la empresa según el
    /// ámbito del documento; <c>null</c> significa que la relación sigue
    /// viva.
    ///
    /// Con <see cref="CriterioInicioRetencion.EventoMasTardio"/> y una
    /// relación todavía abierta devuelve <c>null</c>: no se puede fijar el
    /// inicio del plazo mientras falte uno de los dos eventos, y ante la duda
    /// no se purga.
    /// </summary>
    public static DateOnly? CalcularInicioRetencion(
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        DateOnly? fechaCeseRelacion,
        CriterioInicioRetencion criterio)
    {
        var eventoDocumento = EventoDelDocumento(fechaEmision, fechaVencimiento);

        if (fechaCeseRelacion is not { } cese)
        {
            return criterio == CriterioInicioRetencion.EventoMasTemprano
                ? eventoDocumento
                : null;
        }

        return criterio == CriterioInicioRetencion.EventoMasTemprano
            ? (eventoDocumento <= cese ? eventoDocumento : cese)
            : (eventoDocumento >= cese ? eventoDocumento : cese);
    }

    /// <summary>
    /// Fecha a partir de la cual el documento puede purgarse, o <c>null</c> si
    /// todavía no puede determinarse.
    /// </summary>
    public static DateOnly? CalcularFechaPurga(
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        DateOnly? fechaCeseRelacion,
        int aniosRetencion,
        CriterioInicioRetencion criterio)
    {
        if (aniosRetencion <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(aniosRetencion), "El plazo de retención debe ser de al menos un año.");

        var inicio = CalcularInicioRetencion(fechaEmision, fechaVencimiento, fechaCeseRelacion, criterio);

        return inicio?.AddYears(aniosRetencion);
    }

    /// <summary>
    /// Si el documento ya cumplió su plazo a fecha <paramref name="hoy"/>.
    /// Falla cerrado: mientras no se pueda determinar la fecha de purga,
    /// devuelve false y el documento se conserva.
    /// </summary>
    public static bool PuedePurgarse(
        DateOnly fechaEmision,
        DateOnly? fechaVencimiento,
        DateOnly? fechaCeseRelacion,
        DateOnly hoy,
        int aniosRetencion,
        CriterioInicioRetencion criterio)
    {
        var fechaPurga = CalcularFechaPurga(
            fechaEmision, fechaVencimiento, fechaCeseRelacion, aniosRetencion, criterio);

        return fechaPurga is { } fecha && hoy >= fecha;
    }
}
