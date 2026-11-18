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
/// con una Empresa contraparte es un artefacto interno de la gestión.</item>
/// <item>Sin ninguna de las dos anclas: visible si se cumple cualquiera de estas dos condiciones.
/// <list type="number">
/// <item>La cola de triaje: <see cref="IAlcanceDatosService.TriajeVisibleAsync"/> es verdadero. Ni el rol nulo, ni el
/// rol Cliente, ni un ámbito efectivo acotado ven triaje.</item>
/// <item>El buzón personal propio: la conversación entra por una de las conexiones de
/// <see cref="IAlcanceDatosService.BuzonesPersonalesPropiosAsync"/>, los buzones personales (no compartidos) habilitados
/// del Tenant actual cuyo <c>GestorPropietarioId</c> es el usuario de la sesión, con un ámbito efectivo que concede algo
/// (ADR-011, cartera ∩ operación: sin Asignación de Cartera vigente, la lista es vacía). La decisión 2 del 2026-11-14
/// (DECISION_LOG, «Buzones de varios Tenants…»; propuesta de buzones de varios Tenants § 3.3) dice que el correo de un
/// buzón personal que no se deja como registro «solo vive en la cuenta del Gestor CAE propietario»: por eso lo ve su
/// propietario aunque no alcance el Tenant entero, y nadie más por esta vía. No abre nada más: un buzón compartido sin
/// ancla sigue exigiendo el triaje, y un buzón personal ajeno no está en esa lista.
/// <b>DESVIACIÓN PENDIENTE DE CHRIS</b>: la decisión 2 no dice qué pasa cuando la conexión deja de estar Habilitada.
/// R-3a falla cerrado: con la conexión Deshabilitada, en ConError o eliminada, la lista es vacía y el propietario sin el
/// Tenant entero deja de ver esos hilos en la lista y en el detalle.</item>
/// </list></item>
/// </list>
/// Las dos anclas son excluyentes en el dominio; si una fila trajera las dos, se exigen las dos (falla cerrado).
///
/// <para>
/// El buzón personal ajeno (<c>ConexionIntegracion.GestorPropietarioId</c> de otro gestor) se cierra además fuera de
/// esta expresión, contra la tabla de conexiones y para cualquier ancla: la bandeja de Comunicaciones
/// (<c>ConversacionesVisibles</c>) excluye las conexiones personales ajenas y las eliminadas del
/// Tenant, y el detalle pregunta <see cref="IAlcanceDatosService.ConexionIntegracionVisibleAsync"/>, que falla cerrado
/// con una conexión eliminada o inexistente (esta última, sin clave foránea, sigue en la lista). Los dos aplican la
/// misma regla de propiedad del buzón, con el mismo usuario que la condición 2. Ni el Administrador abre un buzón
/// personal ajeno.
/// </para>
///
/// <para>
/// El alcance viaja como valores ya resueltos: un indicador «sin restricción» y una lista que nunca es nula, para que
/// la traducción a SQL no reciba una colección nula. EF Core convierte cada miembro en un parámetro. Vive en el
/// Núcleo y no conoce la entidad <c>Conversacion</c> (ADR-013: el Núcleo no referencia Comunicaciones): quien filtra
/// pasa los selectores de las dos anclas y del buzón a <see cref="Predicado{T}"/>.
/// </para>
/// </summary>
public sealed class VisibilidadConversacion
{
    private static readonly Expression<Func<VisibilidadConversacion, Guid?, Guid?, Guid?, bool>> Regla =
        (alcance, cliente, empresa, buzon) =>
            (cliente == null || alcance.TodosLosClientes || alcance.Clientes.Contains(cliente.Value))
            && (empresa == null || alcance.TodasLasEmpresas || alcance.Empresas.Contains(empresa.Value))
            && (cliente != null || empresa != null || alcance.Triaje
                || (buzon != null && alcance.BuzonesPersonalesPropios.Contains(buzon.Value)));

    private static readonly Func<VisibilidadConversacion, Guid?, Guid?, Guid?, bool> ReglaCompilada = Regla.Compile();

    private VisibilidadConversacion(
        IReadOnlyList<Guid>? clientes, IReadOnlyList<Guid>? empresas, bool triaje, IReadOnlyList<Guid> buzonesPersonalesPropios)
    {
        TodosLosClientes = clientes is null;
        Clientes = clientes ?? [];
        TodasLasEmpresas = empresas is null;
        Empresas = empresas ?? [];
        Triaje = triaje;
        BuzonesPersonalesPropios = buzonesPersonalesPropios;
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

    /// <summary>
    /// Resultado de <see cref="IAlcanceDatosService.BuzonesPersonalesPropiosAsync"/>: las conexiones del buzón personal
    /// propio. Nunca nula; vacía cierra la condición 2.
    /// </summary>
    public IReadOnlyList<Guid> BuzonesPersonalesPropios { get; }

    /// <summary>
    /// Las cuatro piezas del alcance de quien mira en el Tenant actual, para filtrar una lista. Con el triaje visible no se
    /// piden los buzones personales propios: la regla solo los mira en la rama sin ancla junto al triaje (<c>Triaje ||
    /// buzón propio</c>), así que no cambian nada y su consulta sería una sentencia SQL de más en la bandeja
    /// (presupuesto de <c>EscalaPresupuestoSqlYAislamientoTests</c>). Es el mismo atajo que <see cref="PermiteAsync"/>.
    /// </summary>
    public static async Task<VisibilidadConversacion> ResolverAsync(
        IAlcanceDatosService alcance, CancellationToken cancellationToken = default)
    {
        var clientes = await alcance.ClientesEmpresarialesVisiblesAsync(cancellationToken);
        var empresas = await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);
        var triaje = await alcance.TriajeVisibleAsync(cancellationToken);
        return new(clientes, empresas, triaje, triaje ? [] : await alcance.BuzonesPersonalesPropiosAsync(cancellationToken));
    }

    /// <summary>
    /// La regla para una conversación ya cargada, con sus anclas y su buzón. Solo resuelve las piezas que cuentan para
    /// esas anclas; las demás quedan cerradas (lista vacía, sin triaje, sin buzones propios), y la regla no las mira,
    /// porque cada término solo cuenta con su ancla presente (o, el triaje y el buzón propio, con las dos ausentes).
    /// </summary>
    public static async Task<bool> PermiteAsync(
        IAlcanceDatosService alcance, Guid? cliente, Guid? empresa, Guid? buzon, CancellationToken cancellationToken = default)
    {
        var sinAncla = cliente is null && empresa is null;
        var triaje = sinAncla && await alcance.TriajeVisibleAsync(cancellationToken);
        var visibilidad = new VisibilidadConversacion(
            cliente is null ? [] : await alcance.ClientesEmpresarialesVisiblesAsync(cancellationToken),
            empresa is null ? [] : await alcance.ObtenerEmpresaIdsParaGestionAsync(cancellationToken),
            triaje,
            sinAncla && !triaje && buzon is not null ? await alcance.BuzonesPersonalesPropiosAsync(cancellationToken) : []);
        return visibilidad.Permite(cliente, empresa, buzon);
    }

    /// <summary>La regla evaluada en memoria sobre las anclas y el buzón de una conversación ya cargada.</summary>
    public bool Permite(Guid? cliente, Guid? empresa, Guid? buzon) => ReglaCompilada(this, cliente, empresa, buzon);

    /// <summary>
    /// La misma regla como filtro de <c>IQueryable&lt;T&gt;</c>, traducible a SQL. <paramref name="cliente"/> y
    /// <paramref name="empresa"/> seleccionan las dos anclas de la fila y <paramref name="buzon"/> su conexión de
    /// entrada (en Comunicaciones, <c>Conversacion.ConexionIntegracionId</c>). El buzón es obligatorio en la firma, como
    /// las anclas: un llamador que lo olvidara cerraría el buzón propio sin que nada lo delatara.
    /// </summary>
    public Expression<Func<T, bool>> Predicado<T>(
        Expression<Func<T, Guid?>> cliente, Expression<Func<T, Guid?>> empresa, Expression<Func<T, Guid?>> buzon)
    {
        var fila = Expression.Parameter(typeof(T), "c");
        var sustituciones = new Dictionary<ParameterExpression, Expression>
        {
            [Regla.Parameters[0]] = Expression.Constant(this),
            [Regla.Parameters[1]] = new Sustitucion(new() { [cliente.Parameters[0]] = fila }).Visit(cliente.Body),
            [Regla.Parameters[2]] = new Sustitucion(new() { [empresa.Parameters[0]] = fila }).Visit(empresa.Body),
            [Regla.Parameters[3]] = new Sustitucion(new() { [buzon.Parameters[0]] = fila }).Visit(buzon.Body),
        };
        return Expression.Lambda<Func<T, bool>>(new Sustitucion(sustituciones).Visit(Regla.Body), fila);
    }

    private sealed class Sustitucion(Dictionary<ParameterExpression, Expression> sustituciones) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            sustituciones.TryGetValue(node, out var sustituto) ? sustituto : base.VisitParameter(node);
    }
}
