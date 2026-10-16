using CaeManager.Domain.Proyectos;

namespace CaeManager.Web.Features.Proyectos;

/// <summary>
/// Cuentas de plazo de un proyecto que comparten el panel del listado y la página 360:
/// un solo sitio para que «días abiertos» no dé dos cifras distintas según dónde se mire.
/// </summary>
public static class PlazoProyecto
{
    /// <summary>
    /// Días del periodo abierto del proyecto. Cuenta INCLUSIVA —el día de inicio y el último cuentan los
    /// dos—, la misma que usa la facturación por días de obra abierta
    /// (ObtenerResumenFacturacionQuery, <c>hasta - desde + 1</c>) de las obras: dos cifras de "días abiertos" que no
    /// cuadrasen entre sí serían peor que ninguna. Un evento no se factura, así que no tiene cifra con la que cuadrar.
    /// Sin valor si el proyecto todavía no ha empezado.
    /// <list type="bullet">
    /// <item>Una <see cref="TipoProyecto.Obra"/> cuenta del <paramref name="inicio"/> al cierre real o, si
    /// sigue abierta, a <paramref name="hoy"/>: la obra sin cerrar sigue abierta aunque haya pasado su fin
    /// previsto.</item>
    /// <item>Un <see cref="TipoProyecto.Evento"/> (decisión 2026-10-16: «Centro temporal») cuenta solo entre sus
    /// fechas: del inicio a la fecha de fin prevista, o al cierre real si este llega antes, o a hoy si todavía
    /// está en curso. Un evento terminado y sin cerrar no sigue sumando días cada mañana, y uno futuro no tiene
    /// valor. Es la misma ventana que <see cref="VigenciaEvento"/> usa para mantener activos a los voluntarios.</item>
    /// </list>
    /// </summary>
    public static int? DiasAbiertos(TipoProyecto tipo, DateOnly inicio, DateOnly? finPrevisto, DateOnly? cierre, DateOnly hoy)
    {
        var fin = cierre ?? hoy;
        if (tipo == TipoProyecto.Evento && finPrevisto is { } previsto && previsto < fin)
            fin = previsto;

        return DiasInclusivos(inicio, fin);
    }

    private static int? DiasInclusivos(DateOnly inicio, DateOnly fin) =>
        fin < inicio ? null : fin.DayNumber - inicio.DayNumber + 1;

    /// <summary>
    /// Parte del plazo previsto ya consumida, de 0 a 100, con la misma cuenta inclusiva que
    /// <see cref="DiasAbiertos"/> en el numerador y en el denominador. No distingue el tipo porque no hace falta:
    /// el tope en 100 hace que un evento terminado (cuya ventana acaba en su fin previsto) dé la misma cifra que
    /// una obra pasada de plazo. Sin valor si no hay fin
    /// previsto, si el fin previsto es anterior al inicio o si el proyecto todavía no ha empezado.
    /// </summary>
    public static int? PorcentajeDelPlazo(DateOnly inicio, DateOnly? finPrevisto, DateOnly? cierre, DateOnly hoy)
    {
        if (finPrevisto is not { } fin || fin < inicio || DiasInclusivos(inicio, cierre ?? hoy) is not { } abiertos)
            return null;

        var total = fin.DayNumber - inicio.DayNumber + 1;
        return Math.Clamp((int)Math.Round(abiertos * 100d / total, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>
    /// El cierre con el que hay que pintar el proyecto: su cierre real o, si es un <see cref="TipoProyecto.Evento"/>
    /// abierto cuya fecha de fin ya pasó, esa fecha de fin. Un evento se cierra solo al pasar su fin previsto
    /// (decisión 2026-10-16, <c>CierreEventosTerminadosHostedService</c>, que persiste exactamente esa fecha); entre la
    /// medianoche y el siguiente pase del proceso, la pantalla ya lo muestra cerrado en vez de «fin previsto superado».
    /// </summary>
    public static DateOnly? CierreEfectivo(TipoProyecto tipo, DateOnly? finPrevisto, DateOnly? cierre, DateOnly hoy) =>
        cierre ?? (tipo == TipoProyecto.Evento && finPrevisto is { } fin && fin < hoy ? fin : null);

    /// <summary>
    /// Días que un proyecto ABIERTO lleva pasado de su fin previsto. Sin valor si está cerrado, si aún no
    /// lo ha superado (el propio día del fin previsto no cuenta como retraso) o si no hay plazo que medir
    /// (<see cref="PorcentajeDelPlazo"/>). Es la condición con la que la ficha 360 dice «fin previsto
    /// superado hace N días»: el porcentaje se queda en 100 y no distingue un día de retraso de un mes.
    /// Un <see cref="TipoProyecto.Evento"/> nunca tiene retraso: al pasar su fin previsto se cierra solo
    /// (<see cref="CierreEfectivo"/>).
    /// </summary>
    public static int? DiasDeRetraso(TipoProyecto tipo, DateOnly inicio, DateOnly? finPrevisto, DateOnly? cierre, DateOnly hoy)
    {
        if (CierreEfectivo(tipo, finPrevisto, cierre, hoy) is not null || finPrevisto is not { } fin || PorcentajeDelPlazo(inicio, finPrevisto, cierre, hoy) is null)
            return null;

        var retraso = hoy.DayNumber - fin.DayNumber;
        return retraso > 0 ? retraso : null;
    }
}
