namespace CaeManager.Application.Common;

/// <summary>
/// Descarta lo que <see cref="IAlcanceDatosService"/> haya memoizado en esta instancia Scoped
/// (en Blazor Server, la vida del circuito). El alcance se memoiza a propósito —varios filtros
/// de una misma Query lo piden en cascada—, pero una escritura puede cambiarlo (un Centro, una
/// Asignación Trabajador→Centro, una Relación Empresarial nuevos, una cartera reasignada), y sin
/// invalidar, un circuito largo sigue viendo el alcance anterior a esa escritura.
///
/// La invoca <see cref="InvalidacionAlcanceBehavior{TRequest,TResponse}"/> antes y después de cada Command, así
/// que no hace falta una lista de los Commands que cambian el alcance: invalidar de más solo cuesta
/// recalcular; invalidar de menos deja una visión obsoleta, que es el fallo que corrige. Solo la
/// memoización de esta instancia: no toca el ámbito de Tenant, la cartera ni la autorización.
/// </summary>
public interface IInvalidadorAlcance
{
    void Invalidar();
}

/// <summary>Valor por defecto cuando no hay servicio de alcance memoizado que invalidar (fixtures mínimos).</summary>
public sealed class InvalidadorAlcanceInerte : IInvalidadorAlcance
{
    public void Invalidar() { }
}
