namespace CaeManager.Application.Usuarios;

/// <summary>
/// Lo que la regla «un Tenant no se queda sin Administrador» necesita de la base (decisión
/// del propietario, 2026-10-30): serializar los cambios que retiran un Administrador y
/// saber si queda otro. La aplica <see cref="AutoridadSobreCuentas.VerificarQueNoEsElUltimoAdministradorAsync"/>.
///
/// <para>
/// Las dos llamadas van juntas y dentro de la transacción que escribe: primero el candado,
/// después la lectura. Sin el candado, dos Administradores que se degradan a la vez verían
/// cada uno al otro y el Tenant se quedaría sin ninguno.
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
    /// Si el Tenant tiene, además de <paramref name="usuarioIdExcluido"/>, otra cuenta suya
    /// con rol Administrador que no esté desactivada. Una cuenta pendiente de activación
    /// cuenta: es un Administrador que aún no ha entrado, no la ausencia de uno.
    /// </summary>
    Task<bool> HayOtroAdministradorAsync(Guid tenantId, Guid usuarioIdExcluido, CancellationToken cancellationToken = default);
}
