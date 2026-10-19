using System.Linq.Expressions;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Si una cifra del Cuadro de dirección es de un mes que todavía no ha terminado o de uno ya terminado.
/// </summary>
public enum EstadoCifraMensual
{
    /// <summary>El mes está en curso: la cifra se calcula en vivo y todavía puede cambiar.</summary>
    Parcial,

    /// <summary>
    /// El mes terminó, pero la cifra se ha reconstruido en vivo con los datos de hoy porque todavía no hay
    /// cierre mensual que la congele (decisión 5 del Cuadro de dirección, 2026-10-17).
    /// </summary>
    Estimado,

    /// <summary>Foto persistida al cierre del mes, sin reconstrucción histórica.</summary>
    Existe,
}

/// <summary>
/// El mes natural del Cuadro de dirección, en hora peninsular (Madrid): empieza el día 1 a las 00:00 de
/// Madrid, que es también la hora del cierre mensual (decisión 4, 2026-10-17). Intervalo semiabierto
/// <c>[InicioUtc, FinUtc)</c>.
/// </summary>
public readonly record struct MesCuadroDireccion(int Anyo, int Mes)
{
    /// <summary>Años que el cuadro acepta: anteriores a TALVEG no hay datos, y el límite alto evita desbordar las fechas.</summary>
    public const int AnyoMinimo = 2000;

    public const int AnyoMaximo = 9998;

    /// <summary>
    /// El primer mes con cierre real: octubre de 2026, que se cierra el 1 de noviembre a las 00:00 de Madrid.
    /// Los meses anteriores se cierran reconstruidos y quedan marcados como estimados, aunque el cierre llegue tarde.
    /// </summary>
    public static readonly MesCuadroDireccion PrimerCierreReal = new(2026, 10);

    /// <summary>Si el cierre de este mes es real (desde <see cref="PrimerCierreReal"/>) o una reconstrucción estimada.</summary>
    public bool CierreEsEstimado => new DateOnly(Anyo, Mes, 1) < new DateOnly(PrimerCierreReal.Anyo, PrimerCierreReal.Mes, 1);

    /// <summary>Si el año y el mes forman un mes que el cuadro puede calcular (antes de mirar si ya empezó).</summary>
    public static bool EsValido(int anyo, int mes) =>
        anyo is >= AnyoMinimo and <= AnyoMaximo && mes is >= 1 and <= 12;

    private DateOnly PrimerDia => new(Anyo, Mes, 1);

    public DateTime InicioUtc => DiaDeNegocio.InicioEnUtc(PrimerDia);

    public DateTime FinUtc => DiaDeNegocio.InicioEnUtc(PrimerDia.AddMonths(1));

    /// <summary>Parcial mientras el mes no ha terminado; estimado cuando ya terminó.</summary>
    public EstadoCifraMensual EstadoA(DateTime ahoraUtc) =>
        ahoraUtc < FinUtc ? EstadoCifraMensual.Parcial : EstadoCifraMensual.Estimado;

    /// <summary>La fecha de corte de las cifras «a fecha»: ahora en el mes en curso, el final del mes si ya terminó.</summary>
    public DateTime CorteA(DateTime ahoraUtc) => ahoraUtc < FinUtc ? ahoraUtc : FinUtc;
}

/// <summary>
/// La vida de un registro con borrado lógico (<see cref="EntidadBase"/>): cuándo se dio de alta y, si se
/// eliminó, cuándo. Es la proyección sobre la que se definen activos, altas y bajas del Cuadro de
/// dirección, para que la misma expresión la traduzca EF a SQL y la evalúen las pruebas en memoria.
/// Propiedades con <c>init</c> (no constructor) para que EF pueda componer un <c>Where</c> sobre ella.
/// </summary>
public sealed class VidaDeRegistro
{
    public DateTime CreadoEnUtc { get; init; }
    public bool EstaEliminado { get; init; }
    public DateTime? EliminadoEnUtc { get; init; }

    /// <summary>Documento de identidad del registro cuando es un Trabajador (para contar extranjeros); null en los demás.</summary>
    public string? Documento { get; init; }
}

/// <summary>
/// Definiciones del Cuadro de dirección (decisiones del 2026-10-17 y corrección de Chris sobre el
/// Trabajador activo). Un único punto para cada una: la consulta las aplica en PostgreSQL y las pruebas
/// unitarias las evalúan en memoria con la misma expresión.
///
/// <para>
/// <b>Trabajador activo</b>: sigue dado de alta en TALVEG en su Empresa, tenga o no Asignaciones o
/// Visitas. Solo deja de contar cuando se elimina, sea a mano o por la baja que propone la detección
/// automática de RNT/ITA (<c>ResolverDeteccionAusenteCommand</c>): las dos son el borrado lógico de
/// <see cref="EntidadBase.MarcarComoEliminado"/>. <c>Trabajador.FechaBajaEmpresa</c> es un dato de la
/// ficha y no decide nada aquí. Altas y bajas del mes siguen esa misma definición: alta es
/// <see cref="EntidadBase.CreadoEnUtc"/> dentro del mes, baja es <see cref="EntidadBase.EliminadoEnUtc"/>
/// dentro del mes. Un registro restaurado deja de contar como baja. El Centro de trabajo usa las mismas
/// tres definiciones.
/// </para>
/// </summary>
public static class DefinicionesCuadroDireccion
{
    /// <summary>Existe en el corte: dado de alta antes de él y no eliminado, o eliminado en el corte o después.</summary>
    public static Expression<Func<VidaDeRegistro, bool>> VivoAlCorte(DateTime corteUtc) =>
        v => v.CreadoEnUtc < corteUtc && (!v.EstaEliminado || v.EliminadoEnUtc >= corteUtc);

    /// <summary>Dado de alta dentro del mes, aunque se eliminara después (también dentro del mes).</summary>
    public static Expression<Func<VidaDeRegistro, bool>> AltaEnElMes(DateTime inicioUtc, DateTime finUtc) =>
        v => v.CreadoEnUtc >= inicioUtc && v.CreadoEnUtc < finUtc;

    /// <summary>Eliminado dentro del mes y todavía eliminado.</summary>
    public static Expression<Func<VidaDeRegistro, bool>> BajaEnElMes(DateTime inicioUtc, DateTime finUtc) =>
        v => v.EstaEliminado && v.EliminadoEnUtc >= inicioUtc && v.EliminadoEnUtc < finUtc;

    /// <summary>
    /// Solicitud (decisión 3, 2026-10-17): el primer mensaje entrante de cada conversación. Devuelve su
    /// instante, uno por conversación con algún entrante; los entrantes siguientes de la misma conversación
    /// no son solicitudes nuevas. El mes de la solicitud lo decide ese primer entrante.
    /// </summary>
    public static IQueryable<DateTime> PrimerosEntrantes(this IQueryable<Mensaje> mensajes) =>
        mensajes
            .Where(m => m.Direccion == DireccionMensaje.Entrante)
            .GroupBy(m => m.ConversacionId)
            .Select(g => g.Min(m => m.FechaUtc));

    /// <summary>
    /// Reparto de las solicitudes por día de la semana y hora, en hora peninsular: es cuándo llega el trabajo,
    /// la demanda del Operador CAE, nunca de una persona. Se dan los siete días (los fines de semana también)
    /// para que la pantalla decida qué pinta; solo las franjas con alguna solicitud.
    /// </summary>
    public static IReadOnlyList<FranjaSolicitudesDto> PorFranja(IEnumerable<DateTime> instantesUtc) =>
        instantesUtc
            .Select(i => i.EnHoraPeninsular())
            .GroupBy(h => (h.DayOfWeek, h.Hour))
            .Select(g => new FranjaSolicitudesDto(g.Key.DayOfWeek, g.Key.Hour, g.Count()))
            .OrderBy(f => ((int)f.Dia + 6) % 7)
            .ThenBy(f => f.Hora)
            .ToList();
}
