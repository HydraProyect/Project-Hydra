namespace CaeManager.Application.Usuarios;

/// <summary>
/// Lo que la regla «un Tenant no se queda sin Administrador» necesita de la base (decisión
/// del propietario, 2026-10-30): serializar los cambios que retiran un Administrador y
/// saber si queda otro. La aplica <see cref="AutoridadSobreCuentas.VerificarQueNoEsElUltimoAdministradorAsync"/>.
///
/// <para>
/// Las dos llamadas van juntas y dentro de la transacción que escribe: primero el candado,
/// después la lectura, que decide con los roles de ese momento y no con los que el Command leyó
/// antes de empezar. Sin el candado, dos Administradores que se degradan a la vez verían cada
/// uno al otro y el Tenant se quedaría sin ninguno.
/// </para>
/// </summary>
public interface IAdministradoresDelTenant
{
    /// <summary>
    /// Toma el candado de los Administradores del Tenant hasta el final de la transacción en
    /// curso. Es el mismo que toman las altas de Administrador en la base, así que también
    /// espera a la que esté en vuelo. Sin transacción en curso, lanza.
    /// </summary>
    Task BloquearAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Si <paramref name="usuarioId"/> es, leído ahora, la única cuenta del Tenant con rol
    /// Administrador que no está desactivada. Una cuenta pendiente de activación cuenta: es un
    /// Administrador que aún no ha entrado, no la ausencia de uno. Una cuenta que no es
    /// Administrador, o que ya está desactivada, no es la última: retirarla no cambia nada.
    /// </summary>
    Task<bool> EsElUltimoAdministradorAsync(Guid tenantId, Guid usuarioId, CancellationToken cancellationToken = default);
}
