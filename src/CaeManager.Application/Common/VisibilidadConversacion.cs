using System.Linq.Expressions;

namespace CaeManager.Application.Common;

/// <summary>
/// La regla de visibilidad de una conversación de Comunicaciones por sus anclas, escrita <b>una sola vez</b> (D-6 b,
/// R-3a): la traduce a SQL la lista de la bandeja (<see cref="Predicado{T}"/>) y la evalúa en memoria el detalle y todo
/// consumidor de <see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/> (<see cref="Permite"/>). Es la
/// misma expresión en los dos lados, así que la lista y el detalle no pueden separarse: una conversación sale en la
/// lista si y solo si su detalle se abre (salvo el buzón, abajo).
///
/// <list type="bullet">
/// <item>Ancla Cliente empresarial: en la cartera de Clientes
/// (<see cref="AlcanceDatosServiceExtensions.ClientesEmpresarialesVisiblesAsync"/>).</item>
/// <item>Ancla Empresa contraparte: en el alcance de <b>gestión</b> de Empresas
/// (<see cref="IAlcanceDatosService.ObtenerEmpresaIdsParaGestionAsync"/>), que para el rol Cliente es vacío: un hilo
/// con una contratista es un artefacto interno de la gestión.</item>
/// <item>Sin ninguna de las dos anclas (cola de triaje): solo si <see cref="IAlcanceDatosService.TriajeVisibleAsync"/>
/// es verdadero. Ni el rol nulo, ni el rol Cliente, ni un ámbito efectivo acotado ven triaje.</item>
/// </list>
/// Las dos anclas son excluyentes en el dominio; si una fila trajera las dos, se exigen las dos (falla cerrado).
///
/// <para>
/// El buzón personal ajeno (<c>ConexionIntegracion.GestorPropietarioId</c> de otro gestor) no entra en esta
/// expresión porque se resuelve contra otra tabla: la lista excluye las conexiones personales ajenas del Tenant y el
/// detalle pregunta <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/>, que además falla cerrado con
/// una conexión inexistente o eliminada. Los dos aplican la misma regla de propiedad del buzón.
/// </para>
///
/// <para>
/// El alcance viaja como valores ya resueltos: un indicador «sin restricción» y una lista que nunca es nula, para que
/// la traducción a SQL no reciba una colección nula. EF Core convierte cada miembro en un parámetro. Vive en el
/// Núcleo y no conoce la entidad <c>Conversacion</c> (ADR-013: el Núcleo no referencia Comunicaciones): quien filtra
/// pasa los selectores de las dos anclas a <see cref="Predicado{T}"/>.
/// </para>
/// </summary>
public sealed class VisibilidadConversacion
{
    private static readonly Expression<Func<VisibilidadConversacion, Guid?, Guid?, bool>> Regla =
        (alcance, cliente, empresa) =>
            (cliente == null || alcance.TodosLosClientes || alcance.Clientes.Contains(cliente.Value))
            && (empresa == null || alcance.TodasLasEmpresas || alcance.Empresas.Contains(empresa.Value))
            && (cliente != null || empresa != null || alcance.Triaje);

    private static readonly Func<VisibilidadConversacion, Guid?, Guid?, bool> ReglaCompilada = Regla.Compile();

    private VisibilidadConversacion(IReadOnlyList<Guid>? clientes, IReadOnlyList<Guid>? empresas, bool triaje)
    {
        TodosLosClientes = clientes is null;
        Clientes = clientes ?? [];
        TodasLasEmpresas = empresas is null;
        Empresas = empresas ?? [];
        Triaje = triaje;
    }

    /// <summary>Cartera de Clientes sin restricción (acceso total).</summary>
    public bool TodosLosClientes { get; }

    /// <summary>Cartera de Clientes; vacía con <see cref="TodosLosClientes"/>, y entonces no se consulta.</summary>
    public IReadOnlyList<Guid> Clientes { get; }

    /// <summary>Alcance de gestión de Empresas sin restricción.</summary>
    public bool TodasLasEmpresas { get; }

    /// <summary>Alcance de gestión de Empresas; vacío con <see cref="TodasLasEmpresas"/>, y entonces no se consulta.</summary>
    public IReadOnlyList<Guid> Empresas { get; }

    /// <summary>Resultado de <see cref="IAlcanceDatosService.TriajeVisibleAsync"/>.</summary>
    public bool Triaje { get; }

    /// <summary>Las tres piezas del alcance de quien mira en el Tenant actual, para filtrar una lista.</summary>
    public static async Task<VisibilidadConversacion> ResolverAsync(
        IAlcanceDatosService alcance, CancellationToken cancellationToken = default) =>
        new(await alcance.ClientesEmpresarialesVisiblesAsync(cancellationToken),
            await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken),
            await alcance.TriajeVisibleAsync(cancellationToken));

    /// <summary>
    /// La regla para una conversación ya cargada, con sus anclas. Solo resuelve las piezas que cuentan para esas anclas;
    /// las demás quedan cerradas (lista vacía, sin triaje), y la regla no las mira, porque cada término solo cuenta con
    /// su ancla presente (o, el triaje, con las dos ausentes).
    /// </summary>
    public static async Task<bool> PermiteAsync(
        IAlcanceDatosService alcance, Guid? cliente, Guid? empresa, CancellationToken cancellationToken = default)
    {
        var visibilidad = new VisibilidadConversacion(
            cliente is null ? [] : await alcance.ClientesEmpresarialesVisiblesAsync(cancellationToken),
            empresa is null ? [] : await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken),
            cliente is null && empresa is null && await alcance.TriajeVisibleAsync(cancellationToken));
        return visibilidad.Permite(cliente, empresa);
    }

    /// <summary>La regla evaluada en memoria sobre las anclas de una conversación ya cargada.</summary>
    public bool Permite(Guid? cliente, Guid? empresa) => ReglaCompilada(this, cliente, empresa);

    /// <summary>
    /// La misma regla como filtro de <c>IQueryable&lt;T&gt;</c>, traducible a SQL. <paramref name="cliente"/> y
    /// <paramref name="empresa"/> seleccionan las dos anclas de la fila (en Comunicaciones, las de la conversación).
    /// </summary>
    public Expression<Func<T, bool>> Predicado<T>(Expression<Func<T, Guid?>> cliente, Expression<Func<T, Guid?>> empresa)
    {
        var fila = Expression.Parameter(typeof(T), "c");
        var sustituciones = new Dictionary<ParameterExpression, Expression>
        {
            [Regla.Parameters[0]] = Expression.Constant(this),
            [Regla.Parameters[1]] = new Sustitucion(new() { [cliente.Parameters[0]] = fila }).Visit(cliente.Body),
            [Regla.Parameters[2]] = new Sustitucion(new() { [empresa.Parameters[0]] = fila }).Visit(empresa.Body),
        };
        return Expression.Lambda<Func<T, bool>>(new Sustitucion(sustituciones).Visit(Regla.Body), fila);
    }

    private sealed class Sustitucion(Dictionary<ParameterExpression, Expression> sustituciones) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            sustituciones.TryGetValue(node, out var sustituto) ? sustituto : base.VisitParameter(node);
    }
}
