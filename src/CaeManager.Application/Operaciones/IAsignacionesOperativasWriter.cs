using CaeManager.Domain.Operaciones;

namespace CaeManager.Application.Operaciones;

/// <summary>
/// Escribe el reparto de responsabilidad operativa en las tablas de asignación,
/// en paralelo a <c>DelegacionTenant</c>, que se conserva como proyección de
/// compatibilidad. El reparto por Cliente empresarial está retirado (D-7,
/// 2026-10-02): una Asignación de Cartera es siempre sobre el Tenant entero, y
/// <c>Empresa.EjecutivoUsuarioId</c> es una referencia, no una fuente de cartera.
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
    /// Concede a un Gestor CAE la cartera del <b>Tenant propietario entero</b>: una Asignación de
    /// Cartera universal (ADR-011 § 2.7, enmienda 2026-09-23 y D-7, 2026-10-02). Es un acto
    /// explícito de quien lo llama —una siembra, hoy—, no la consecuencia de ser la referencia
    /// (<c>Empresa.EjecutivoUsuarioId</c>) de un Cliente empresarial: la referencia no concede
    /// alcance. El reparto por Cliente empresarial está retirado y ya no se escribe.
    ///
    /// <para>
    /// Idempotente: si el Gestor CAE ya tiene una cartera universal vigente sobre la operación que
    /// le corresponde, no hace nada. El tenant propietario lo indica quien llama (no se lee del
    /// contexto) y nunca se cierra nada. Solo concede Operación: la cartera no da Administrador ni
    /// Dirección CAE.
    /// </para>
    ///
    /// <para>
    /// <b>Marca de principal</b> (ADR-011 § 2.7, enmienda 2026-10-08): la cartera nace principal si su
    /// Asignación de Operación no tiene ninguna principal viva; si ya la tiene, nace sin marca.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Usuario inexistente, o sin operación vigente donde colgar la cartera: la raíz del Tenant
    /// propietario si el usuario es de ese Tenant, la operación externa de su Operador CAE si no.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// La cuenta no es un Gestor CAE: por Identity si es del Tenant propietario, o por su
    /// Asignación de Operador Delegado única y vigente —fallo cerrado, nunca un rol por omisión— si
    /// llega por un Operador CAE externo.
    /// </exception>
    Task AsegurarCarteraTenantEnteroAsync(
        Guid propietarioTenantId, Guid gestorUsuarioId, CancellationToken cancellationToken = default);

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
    /// Cierra <b>todas</b> las Asignaciones de Operación Outbound externas no cerradas del Operador
    /// CAE externo sobre el Tenant propietario —la del Tenant entero y las acotadas a un Cliente
    /// empresarial, vigentes, programadas o suspendidas— de una delegación que se desactiva, y con
    /// ellas todas sus carteras no cerradas, en cualquier estado: una cartera no puede sobrevivir a la
    /// operación que la ampara, y en duda la revocación falla cerrada (decisión de Chris del
    /// 2026-11-14). Nada de lo cerrado vuelve al reactivar la delegación: la reactivación abre una
    /// operación nueva sin carteras, y cada una nace de un acto explícito (opción A en la PR #2223).
    /// </summary>
    Task CerrarOperacionDelegadaAsync(
        Guid propietarioTenantId, Guid operadorTenantId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Abre la cartera de un operador delegado sobre una operación externa.
    ///
    /// <b>Solo concede Coordinador CAE, Gestor CAE o Consulta</b> (decisión del
    /// propietario, 2026-09-23). Administrador y Dirección CAE son autoridad
    /// de Propiedad del Tenant propietario y ninguna cartera ni delegación la
    /// concede: con cualquier otro rol lanza <see cref="UnauthorizedAccessException"/>
    /// sin abrir nada.
    ///
    /// <b>El ámbito lo decide el rol, no la comodidad.</b> Consulta, el único
    /// rol de alcance total que se puede conceder aquí, ve todo el workspace
    /// por su rol, así que su cartera es universal y no le añade nada. Un rol de
    /// cartera (Gestor CAE, Coordinador CAE) no recibe aquí cartera: darle una
    /// universal sería concederle de golpe el Tenant propietario entero sin que
    /// nadie lo decidiera. La cartera de un Gestor CAE nace de un acto
    /// explícito —una solicitud de incorporación aceptada, el alta o la
    /// asignación de empresas en Usuarios, o <see cref="AsegurarCarteraTenantEnteroAsync"/>
    /// en una siembra—; la de un Coordinador CAE se deriva de las de sus Gestores CAE.
    ///
    /// <b>Lo suspendido nunca vuelve como vigente.</b> Tampoco emite la de Consulta si la persona
    /// tiene en el par una cartera viva que hoy no autoriza —Suspendida, Programada o bajo una
    /// operación que no está Vigente— (<see cref="AsignacionCartera.HayAlgunaVivaQueNoAutoriza"/>):
    /// reanudarla es otro acto. Solo lo llaman actos explícitos; la reactivación de una delegación ya
    /// no emite carteras.
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
    /// Cierra las carteras no cerradas de un operador delegado al que se le revoca la asignación,
    /// bajo cualquier Asignación de Operación Outbound externa del par (del Tenant entero o acotada,
    /// en cualquier estado): retirar a la persona le quita toda autorización Outbound derivada.
    /// </summary>
    Task CerrarCarteraOperadorAsync(
        Guid propietarioTenantId, Guid operadorTenantId, Guid usuarioId, MotivoCierreAsignacion motivo,
        CancellationToken cancellationToken = default);
}
