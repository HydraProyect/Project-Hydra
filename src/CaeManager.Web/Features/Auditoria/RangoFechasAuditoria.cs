using CaeManager.Domain.Common;

namespace CaeManager.Web.Features.Auditoria;

/// <summary>
/// Rango de fechas con el que la pantalla de Auditoría consulta una tabla particionada por
/// <c>FechaUtc</c> (M-21): sin cota inferior, el recuento y el <c>Skip</c> recorren todas las
/// particiones. Las fechas son días de negocio en hora peninsular (lo que ve y teclea quien consulta, y la
/// misma zona en que la tabla pinta cada fila); la conversión a UTC es <see cref="DiaDeNegocio"/>.
///
/// <para>
/// El rango por defecto lo pone <b>la pantalla</b>, no la consulta: <c>ObtenerAuditoriaQuery</c>
/// sin fechas sigue devolviendo todo el historial, que es lo que esperan la pestaña Historial de
/// cada entidad y la exportación sin parámetros. Solo «Desde» lleva valor por defecto; «Hasta»
/// vacío significa «hasta ahora», de modo que lo registrado hoy nunca queda fuera.
/// </para>
/// </summary>
public static class RangoFechasAuditoria
{
    public const int DiasPorDefecto = 90;
    public const string FormatoFecha = "yyyy-MM-dd";

    public static DateOnly DesdePorDefecto() => DiaDeNegocio.Hoy().AddDays(-DiasPorDefecto);

    /// <summary>
    /// Texto de un campo de fecha (<c>yyyy-MM-dd</c>) a fecha; vacío, inválido o con un año fuera de
    /// 1900–2999 es «sin límite» (un año de 5 o 6 dígitos, que el navegador admite, no debe llegar al
    /// cálculo de días ni a la base de datos).
    /// </summary>
    public static DateOnly? Interpretar(string? texto) =>
        DateOnly.TryParseExact(texto, FormatoFecha, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var fecha) && fecha.Year is >= 1900 and <= 2999
            ? fecha
            : null;

    /// <summary>Primer instante UTC del día <paramref name="desde"/> (inclusivo).</summary>
    public static DateTime? DesdeUtc(DateOnly? desde) =>
        desde is null ? null : DiaDeNegocio.InicioEnUtc(desde.Value);

    /// <summary>Primer instante UTC del día siguiente a <paramref name="hasta"/> (exclusivo): el día elegido entra entero.</summary>
    public static DateTime? HastaUtcExclusivo(DateOnly? hasta) =>
        hasta is null ? null : DiaDeNegocio.InicioEnUtc(hasta.Value.AddDays(1));
}
