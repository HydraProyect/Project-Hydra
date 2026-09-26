namespace CaeManager.Application.Clientes;

/// <summary>
/// Serializa, por cuenta, lo que cambia la cartera de un Gestor CAE (revisión Codex del
/// incremento B de FS-25, P1). Sin esto, en READ COMMITTED, una reasignación concurrente
/// podía asignar un Cliente empresarial a un Gestor CAE —o quitárselo— entre la última
/// lectura de su cartera y el COMMIT de la desactivación con traspaso: la cuenta quedaba
/// desactivada con cartera, o el traspaso movía algo que ya no era suyo.
///
/// <para>
/// El candado dura lo que la transacción que lo toma (<c>pg_advisory_xact_lock</c>), así
/// que <b>solo vale dentro de una</b> (<see cref="Common.ITransaccionDeComando"/>): fuera de
/// ella la implementación lanza, para que un uso sin transacción no pase por protegido.
/// </para>
/// </summary>
public interface IBloqueoCarteraUsuario
{
    /// <summary>
    /// Exclusivo: nadie más reasigna hacia ni desde esta cuenta hasta el final de la
    /// transacción. Lo toma quien la desactiva pasando su cartera.
    /// </summary>
    Task BloquearExclusivoAsync(Guid usuarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compartido: dos reasignaciones que tocan la misma cuenta no se esperan entre sí, pero
    /// sí esperan a una desactivación con traspaso en curso sobre ella.
    /// </summary>
    Task BloquearCompartidoAsync(IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default);
}
