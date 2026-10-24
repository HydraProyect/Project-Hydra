namespace CaeManager.Application.EstadoMaterializado;

/// <summary>
/// La marca «recalculando» del estado materializado del Tenant de la conexión (diseño § 13.8): qué está esperando en la cola
/// o en el relleno, para que un lector (PR 6 y 7 del plan) no presente como definitivo un estado que aún va a cambiar.
/// <see cref="TenantEntero"/> es cierto si el Tenant aún no tiene versión (no se ha rellenado) o tiene una entrada de Tenant
/// entero en la cola; si no, las raíces pendientes dicen qué Centros, Trabajadores y Empresas esperan.
///
/// <para>
/// Se deriva de la cola y de la versión, sin columna propia: una entrada sigue en la cola hasta que confirma la transacción
/// que la procesa, así que la marca dura exactamente lo que dura el recálculo.
/// </para>
/// </summary>
public sealed record MarcaRecalculandoEstado(
    bool TenantEntero, IReadOnlyCollection<Guid> Centros, IReadOnlyCollection<Guid> Trabajadores, IReadOnlyCollection<Guid> Empresas)
{
    public static MarcaRecalculandoEstado Ninguna { get; } = new(false, [], [], []);

    public bool Recalculando => TenantEntero || Centros.Count > 0 || Trabajadores.Count > 0 || Empresas.Count > 0;
}

/// <inheritdoc cref="MarcaRecalculandoEstado"/>
public interface ILectorMarcaRecalculandoEstado
{
    Task<MarcaRecalculandoEstado> LeerAsync(CancellationToken cancellationToken);
}
