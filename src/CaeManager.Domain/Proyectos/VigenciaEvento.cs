using System.Linq.Expressions;

namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Única definición del criterio de vigencia de un Proyecto de tipo Evento. La usan a la vez la regla en memoria
/// (<see cref="Proyecto.EventoTerminadoEn"/>, <see cref="Proyecto.EventoAportaRequisitosEn"/>) y las consultas traducidas a
/// SQL de Application (<c>ActividadVoluntarios</c>, <c>RequisitosDeEventos</c>, los Trabajadores del Centro), así que
/// cambiar de criterio es cambiar una sola expresión.
/// <list type="bullet">
/// <item><see cref="SinTerminarEn"/>: evento no cerrado y con su fecha de fin prevista no pasada (vigente o futuro). Es lo
/// que mantiene activo a un voluntario (decisión 2026-10-15).</item>
/// <item><see cref="AportaRequisitosEn"/>: cuándo sus requisitos (<see cref="ProyectoRequisito"/>) cuentan para el
/// cumplimiento de los Trabajadores asignados a él. Desde la decisión del 2026-10-16 (opción B) es el mismo criterio que
/// <see cref="SinTerminarEn"/>: cuentan desde la asignación al evento mientras es futuro o vigente, no solo desde su
/// <c>FechaInicio</c>, para que quien entra llegue al evento con la documentación preparada.</item>
/// </list>
/// </summary>
public static class VigenciaEvento
{
    private static readonly Expression<Func<Proyecto, DateOnly, bool>> SinTerminar = (proyecto, hoy) =>
        proyecto.Tipo == TipoProyecto.Evento && proyecto.FechaCierreReal == null && proyecto.FechaFinPrevista >= hoy;

    private static readonly Func<Proyecto, DateOnly, bool> SinTerminarCompilada = SinTerminar.Compile();

    /// <summary>Predicado traducible a SQL: evento sin terminar (vigente o futuro) en <paramref name="hoy"/>.</summary>
    public static Expression<Func<Proyecto, bool>> SinTerminarEn(DateOnly hoy) => FijarDia(SinTerminar, hoy);

    /// <summary>
    /// Predicado traducible a SQL: evento cuyos requisitos cuentan en <paramref name="hoy"/>. Coincide con
    /// <see cref="SinTerminarEn"/> (opción B, 2026-10-16): un evento futuro ya se los pide a quien está asignado a él.
    /// </summary>
    public static Expression<Func<Proyecto, bool>> AportaRequisitosEn(DateOnly hoy) => FijarDia(SinTerminar, hoy);

    internal static bool EstaSinTerminar(Proyecto proyecto, DateOnly hoy) => SinTerminarCompilada(proyecto, hoy);

    /// <summary>
    /// Sustituye el parámetro del día por el acceso a un campo de un objeto capturado, igual que hace el compilador con una
    /// variable local: EF Core lo envía como parámetro SQL en vez de incrustar la fecha como literal.
    /// </summary>
    private static Expression<Func<Proyecto, bool>> FijarDia(Expression<Func<Proyecto, DateOnly, bool>> criterio, DateOnly hoy)
    {
        var dia = Expression.Field(Expression.Constant(new DiaCapturado(hoy)), nameof(DiaCapturado.Valor));
        var cuerpo = new SustituirParametro(criterio.Parameters[1], dia).Visit(criterio.Body);
        return Expression.Lambda<Func<Proyecto, bool>>(cuerpo, criterio.Parameters[0]);
    }

    private sealed class DiaCapturado(DateOnly valor)
    {
        public readonly DateOnly Valor = valor;
    }

    private sealed class SustituirParametro(ParameterExpression parametro, Expression sustituto) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == parametro ? sustituto : node;
    }
}
