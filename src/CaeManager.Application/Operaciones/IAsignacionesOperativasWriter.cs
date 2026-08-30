using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Escribe el reparto de responsabilidad operativa en las tablas de asignación,
/// en paralelo a los mecanismos antiguos que se conservan como proyección de
/// compatibilidad (<c>Cliente.EjecutivoUsuarioId</c> y <c>DelegacionTenant</c>).
///
/// <b>Ningún método guarda.</b> Todos dejan las entidades en el contexto para
/// que el <c>SaveChangesAsync</c> del propio comando las confirme junto al
/// cambio del mecanismo antiguo: así la doble escritura es transaccional sin
/// que ningún comando tenga que abrir una transacción explícita. Si el comando
/// falla, no queda un reparto a medias.
///
/// <b>Los fallos se lanzan, no se registran.</b> Desde que los lectores de
/// autorización leen de estas tablas, escribir solo la proyección y seguir
/// adelante deja al usuario sin el acceso que el comando dice haberle dado, sin
/// error en pantalla y hasta el siguiente arranque. Un comando que no puede
/// escribir su mitad nueva tiene que fallar entero.
///
/// Todo lo que escribe es <b>append-only</b>: reasignar no edita una fila, la
/// cierra y abre otra.
/// </summary>
public interface IAsignacionesOperativasWriter
{
    /// <summary>
    /// Traslada un cambio de ejecutivo de cliente. Cierra las carteras vigentes
    /// sobre ese cliente y abre la del nuevo, si lo hay. El tenant propietario
    /// es siempre el del contexto actual: reasignar ocurre dentro del workspace
    /// del propietario, tanto si lo hace su propio equipo como un operador
    /// delegado.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Si no puede escribirse la cartera del nuevo ejecutivo: sin tenant
    /// resuelto, usuario inexistente, o sin operación vigente donde colgarla.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Si el nuevo ejecutivo pertenece al tenant operador pero no tiene una
    /// asignación de Operador Delegado única y vigente sobre este tenant
    /// propietario — fallo cerrado, nunca se le concede un rol por omisión.
    /// </exception>
    Task ReasignarCarteraClienteAsync(
        Guid clienteId, Guid? nuevoEjecutivoUsuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Garantiza que un tenant tiene su operación raíz. Se invoca al dar de
    /// alta un tenant, que es cuando nace su derecho a operarse a sí mismo.
    /// </summary>
    Task AsegurarOperacionRaizAsync(
        Guid propietarioTenantId, DateTime vigenciaDesde, CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre la operación externa que corresponde a una delegación comercial que
    /// se activa, y <b>devuelve la instancia</b>.
    ///
    /// Devolverla no es una comodidad: la fila queda solo añadida al contexto,
    /// sin guardar, así que una consulta LINQ posterior —que va a SQL— no la
    /// encuentra. Quien necesite colgarle una cartera en el mismo comando tiene
    /// que usar esta instancia, no volver a buscarla.
    /// </summary>
    Task<AsignacionOperacion> AbrirOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, DateTime vigenciaDesde, DateTime? vigenciaHasta,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cierra la operación externa de una delegación que se desactiva, y con
    /// ella todas sus carteras: una cartera no puede sobrevivir a la operación
    /// que la ampara.
    /// </summary>
    Task CerrarOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre la cartera de un operador delegado sobre una operación externa.
    ///
    /// <b>El ámbito lo decide el rol, no la comodidad.</b> Un rol de alcance
    /// total (Administrador, DireccionCae, Consulta) ve todo el workspace por
    /// su rol, así que su cartera es universal y no le añade nada. Un rol de
    /// cartera (GestorCae, CoordinadorCae) ve exactamente los clientes que
    /// tenga asignados: darle una cartera universal le ensancharía el alcance
    /// respecto a lo que tiene hoy, y F1 no cambia comportamiento. Sus carteras
    /// las crea <see cref="ReasignarCarteraClienteAsync"/> cliente a cliente.
    /// </summary>
    /// <param name="operacion">
    /// La operación sobre la que cuelga, ya sea recién creada en este mismo
    /// comando o cargada de la base de datos.
    /// </param>
    Task AbrirCarteraOperadorAsync(
        AsignacionOperacion operacion, Guid usuarioId, string rol, CancellationToken cancellationToken = default);

    /// <summary>
    /// Variante que resuelve la operación externa vigente por sus dos tenants.
    /// Falla si no la encuentra: quien la llama está afirmando que existe.
    /// </summary>
    Task AbrirCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, string rol,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconstruye, sobre una operación recién reabierta, las carteras de los
    /// operadores delegados que siguen autorizados por la vía heredada.
    ///
    /// Hace falta porque desactivar una delegación cierra la operación <b>y sus
    /// carteras en cascada</b>, pero no borra las filas de operador delegado:
    /// sin esto, reactivar dejaría una operación vigente con cero carteras y el
    /// operador entraría al workspace sin ver ningún dato.
    /// </summary>
    Task ReabrirCarterasDeOperadoresAsync(
        AsignacionOperacion operacion, Guid delegacionTenantId, CancellationToken cancellationToken = default);

    /// <summary>Cierra la cartera de un operador delegado al que se le revoca la asignación.</summary>
    Task CerrarCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default);
}
