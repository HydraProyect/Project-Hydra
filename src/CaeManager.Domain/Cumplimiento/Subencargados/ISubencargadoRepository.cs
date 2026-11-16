namespace CaeManager.Domain.Cumplimiento.Subencargados;

/// <summary>
/// Persistencia de los subencargados (catálogo de Plataforma), de sus avisos y de sus objeciones (#2010, D-7).
/// Lo que lee o escribe filas de un Tenant propietario lo hace en el Tenant actual: quien llama abre antes el
/// ámbito de ese Tenant (<c>AmbitoTenantExplicito</c>) y el filtro global y la RLS lo acotan a él. Ninguna lectura
/// cruza Tenants.
/// </summary>
public interface ISubencargadoRepository
{
    void Agregar(Subencargado subencargado);

    /// <summary>Con seguimiento, para cambiar su estado.</summary>
    Task<Subencargado?> ObtenerAsync(Guid subencargadoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Los Tenants propietarios activos a los que hay que avisar: <c>Estado = Activo</c> y que no sean el Tenant de
    /// Plataforma. La suspensión comercial no los excluye (es un eje aparte del estado del Tenant).
    /// </summary>
    Task<IReadOnlyList<Guid>> TenantsPropietariosActivosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Si el Tenant actual tiene un aviso <see cref="FaseAvisoCambioSubencargado.Enviado"/> de ese subencargado,
    /// registrado como muy tarde en <paramref name="enviadoComoTardeEnUtc"/> si se indica.
    /// </summary>
    Task<bool> TieneAvisoProbadoAsync(Guid subencargadoId, DateTime? enviadoComoTardeEnUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Los Administradores con cuenta activa y correo del Tenant propietario actual: solo las cuentas de ese Tenant,
    /// no las de quien lo opera desde otro.
    /// </summary>
    Task<IReadOnlyList<DestinatarioAvisoSubencargado>> AdministradoresDelTenantActualAsync(
        Guid tenantId, CancellationToken cancellationToken = default);

    void AgregarAviso(AvisoCambioSubencargado aviso);

    /// <summary>Si el Tenant actual ya objetó ese subencargado.</summary>
    Task<bool> ExisteObjecionAsync(Guid subencargadoId, CancellationToken cancellationToken = default);

    void AgregarObjecion(ObjecionSubencargado objecion);

    /// <summary>
    /// Si el Tenant propietario objetó un subencargado <see cref="EstadoSubencargado.Activo"/> que se excluye por ese
    /// conmutador: entonces ese Tenant deja de usarlo (D-7). Antes de activarse ese Tenant todavía no lo usa, y una
    /// vez retirado ya no lo usa nadie, así que en ninguno de los dos casos la objeción apaga nada. Lee en el Tenant
    /// actual, como la instrucción de tratamiento con la que se combina.
    /// </summary>
    Task<bool> ExcluyeConmutadorAsync(Guid tenantId, ConmutadorExclusionSubencargado conmutador, CancellationToken cancellationToken = default);

    /// <summary>Si la unidad de trabajo está dentro de una transacción: el aviso no se envía así.</summary>
    bool HayTransaccionAbierta { get; }

    /// <summary>Si la unidad de trabajo tiene cambios sin guardar: el aviso no se envía así.</summary>
    bool HayCambiosPendientes { get; }
}

/// <summary>A quién se envía un aviso: la cuenta y su correo. El correo no se guarda en el aviso.</summary>
public sealed record DestinatarioAvisoSubencargado(Guid UsuarioId, string Email);
