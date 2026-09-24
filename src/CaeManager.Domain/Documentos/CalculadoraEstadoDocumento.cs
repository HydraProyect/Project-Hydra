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
    /// Recibe la vigencia explícita del Documento, no una fecha nullable: una
    /// fecha nula significaba a la vez «no caduca» y «nadie lo ha anotado», y
    /// las dos salían como <see cref="EstadoDocumento.SinCaducidad"/>. Ahora:
    /// <list type="bullet">
    /// <item><c>NoCaduca</c> → <see cref="EstadoDocumento.SinCaducidad"/>.</item>
    /// <item><c>SinConfirmar</c> → <see cref="EstadoDocumento.SinConfirmar"/>:
    /// no se sabe, y «no lo sé» no es «vigente».</item>
    /// <item><c>VenceEnFecha</c> → semáforo por días restantes y umbrales. La
    /// fecha puede venir del vencimiento automático del TipoDocumento o de una
    /// fecha introducida a mano; participa igual.</item>
    /// </list>
    /// </summary>
    public static EstadoDocumento Calcular(
        VigenciaDocumento vigencia,
        DateOnly hoy,
        int umbralAmbarDias,
        int umbralRojoDias)
    {
        if (vigencia.Estado == EstadoVigenciaDocumento.NoCaduca)
            return EstadoDocumento.SinCaducidad;

        if (vigencia.FechaVencimiento is not { } fechaVencimiento)
            return EstadoDocumento.SinConfirmar;

        var diasRestantes = fechaVencimiento.DayNumber - hoy.DayNumber;

        if (diasRestantes < 0)
            return EstadoDocumento.Vencido;

        if (diasRestantes <= umbralRojoDias)
            return EstadoDocumento.Urgente;

        if (diasRestantes <= umbralAmbarDias)
            return EstadoDocumento.Proximo;

        return EstadoDocumento.Vigente;
    }

    /// <summary>
    /// Atajo para las proyecciones que leen las dos columnas del Documento
    /// (<c>EstadoVigencia</c>, <c>FechaVencimiento</c>) en vez del agregado.
    /// Rehidrata con la misma validación: una fila incoherente revienta.
    /// </summary>
    public static EstadoDocumento Calcular(
        EstadoVigenciaDocumento estadoVigencia,
        DateOnly? fechaVencimiento,
        DateOnly hoy,
        int umbralAmbarDias,
        int umbralRojoDias) =>
        Calcular(VigenciaDocumento.Rehidratar(estadoVigencia, fechaVencimiento), hoy, umbralAmbarDias, umbralRojoDias);

    /// <summary>
    /// Decide la vigencia con la que nace o se renueva un Documento. Es la
    /// única regla para todos los productores (alta, renovación, IA,
    /// importación, plantillas):
    /// <list type="bullet">
    /// <item>TipoDocumento con vencimiento automático → vence en
    /// <c>fechaEmision + vigenciaMeses</c>; lo manual se ignora.</item>
    /// <item>Si no, una fecha anotada a mano → vence esa fecha.</item>
    /// <item>Si no, «no caduca» solo si alguien lo ha confirmado
    /// expresamente (<paramref name="noCaducaConfirmado"/>).</item>
    /// <item>Si no, <see cref="VigenciaDocumento.SinConfirmar"/>. Nunca se
    /// deduce «no caduca» de la ausencia de fecha.</item>
    /// </list>
    /// </summary>
    public static VigenciaDocumento ResolverVigencia(
        bool aplicaVencimientoAutomatico,
        int? vigenciaMeses,
        DateOnly fechaEmision,
        DateOnly? fechaVencimientoManual,
        bool noCaducaConfirmado)
    {
        if (aplicaVencimientoAutomatico)
            return VigenciaDocumento.DesdeFechaOpcional(CalcularFechaVencimiento(fechaEmision, vigenciaMeses));

        if (fechaVencimientoManual is not null && noCaducaConfirmado)
            throw new ArgumentException(
                "Un documento no puede tener fecha de vencimiento y a la vez estar confirmado como que no caduca.",
                nameof(noCaducaConfirmado));

        if (fechaVencimientoManual is { } fecha)
            return VigenciaDocumento.VenceEl(fecha);

        return noCaducaConfirmado ? VigenciaDocumento.NoCaduca : VigenciaDocumento.SinConfirmar;
    }

    /// <summary>
    /// Calcula la fecha de vencimiento de un documento a partir de su fecha de
    /// emisión y la vigencia en meses del TipoDocumento (o de la periodicidad
    /// especial de un RequisitoDocumental, si el llamador la pasa en su lugar).
    /// </summary>
    public static DateOnly? CalcularFechaVencimiento(DateOnly fechaEmision, int? vigenciaMeses) =>
        vigenciaMeses.HasValue ? fechaEmision.AddMonths(vigenciaMeses.Value) : null;
}
