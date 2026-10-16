using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;

namespace CaeManager.Application.DocumentosIa.ValidacionPrevia;

/// <summary>Reglas síncronas de validación previa documental.</summary>
public enum ReglaValidacionPrevia { C0, C1 }

/// <summary>Salta indica una propuesta; Aviso no constituye rechazo.</summary>
public enum ResultadoReglaValidacionPrevia { Superada, Salta, Aviso, NoEvaluable }

/// <summary>Detalle cerrado de la comprobación que no se supera.</summary>
public enum DetalleValidacionPrevia
{
    ArchivoVacio, ExcedeTamano, FirmaNoCoincide, PdfNoAbre, SinPaginas,
    VencimientoAnteriorAHoy, VencimientoAusenteEnTipoQueCaduca,
}

/// <summary>Propuesta documental, sin aplicar ningún cambio de estado.</summary>
public enum VeredictoPropuestoValidacionPrevia { Incompleto, Invalidar }

/// <summary>
/// Hechos de C0 obtenidos por el llamador. La firma debe haberse comprobado frente
/// al formato declarado y admitido; un formato no admitido produce false.
/// PdfAbre y NumeroPaginas solo se consultan si EsPdf es true.
/// </summary>
public sealed record DatosFormatoValidacionPrevia(
    long TamanoBytes, bool FirmaCoincideConFormatoAdmitido, bool EsPdf,
    bool PdfAbre, int NumeroPaginas);

/// <summary>Hechos de C1; Hoy se inyecta y nunca se obtiene del reloj aquí.</summary>
public sealed record DatosFechaValidacionPrevia(
    DateOnly? VencimientoDeclarado, bool TipoCaduca, DateOnly Hoy);

/// <summary>Resultado independiente; causa y veredicto solo existen cuando la regla salta.</summary>
public sealed record ResultadoReglaPrevia(
    ReglaValidacionPrevia Regla, ResultadoReglaValidacionPrevia Resultado,
    DetalleValidacionPrevia? Detalle = null, CausaRechazoAcreditacion? Causa = null,
    VeredictoPropuestoValidacionPrevia? VeredictoPropuesto = null);

/// <summary>Evaluador puro de C0 y C1 sobre hechos ya extraídos, sin leer archivos ni reloj.</summary>
public static class ValidacionesDeterministasDocumento
{
    /// <summary>Devuelve siempre ambas reglas, en orden C0, C1, sin cortocircuitarlas.</summary>
    public static IReadOnlyList<ResultadoReglaPrevia> Evaluar(
        DatosFormatoValidacionPrevia formato, DatosFechaValidacionPrevia fecha) =>
        [EvaluarFormato(formato), EvaluarFecha(fecha)];

    /// <summary>Comprueba C0 sin necesitar ningún dato de C1.</summary>
    public static ResultadoReglaPrevia EvaluarFormato(DatosFormatoValidacionPrevia datos)
    {
        // El diseño no prioriza fallos simultáneos de C0: se informa del primero
        // en el orden de comprobación publicado. Un tamaño no positivo se trata como vacío.
        DetalleValidacionPrevia? detalle = datos switch
        {
            { TamanoBytes: <= 0 } => DetalleValidacionPrevia.ArchivoVacio,
            { TamanoBytes: > LimitesArchivoSubido.TamanoMaximoBytes } => DetalleValidacionPrevia.ExcedeTamano,
            { FirmaCoincideConFormatoAdmitido: false } => DetalleValidacionPrevia.FirmaNoCoincide,
            { EsPdf: true, PdfAbre: false } => DetalleValidacionPrevia.PdfNoAbre,
            { EsPdf: true, NumeroPaginas: <= 0 } => DetalleValidacionPrevia.SinPaginas,
            _ => null,
        };

        return detalle is null
            ? new(ReglaValidacionPrevia.C0, ResultadoReglaValidacionPrevia.Superada)
            : new(ReglaValidacionPrevia.C0, ResultadoReglaValidacionPrevia.Salta, detalle,
                CausaRechazoAcreditacion.FormatoNoAdmitido, VeredictoPropuestoValidacionPrevia.Incompleto);
    }

    /// <summary>
    /// Comprueba C1 sin necesitar ningún dato de C0. Hoy todavía es vigente. En un tipo que
    /// no caduca la fecha declarada se ignora (el diseño no lo prevé: decisión conservadora,
    /// pendiente de confirmar). El aviso por fecha ausente es solo informativo: no lleva
    /// causa ni veredicto hasta que lo fije la ruta IA (C5).
    /// </summary>
    public static ResultadoReglaPrevia EvaluarFecha(DatosFechaValidacionPrevia datos)
    {
        if (!datos.TipoCaduca)
            return new(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.NoEvaluable);

        if (datos.VencimientoDeclarado is not { } vencimiento)
            return new(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.Aviso,
                DetalleValidacionPrevia.VencimientoAusenteEnTipoQueCaduca);

        return vencimiento < datos.Hoy
            ? new(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.Salta,
                DetalleValidacionPrevia.VencimientoAnteriorAHoy,
                CausaRechazoAcreditacion.CaducadoAlPresentar, VeredictoPropuestoValidacionPrevia.Invalidar)
            : new(ReglaValidacionPrevia.C1, ResultadoReglaValidacionPrevia.Superada);
    }
}
