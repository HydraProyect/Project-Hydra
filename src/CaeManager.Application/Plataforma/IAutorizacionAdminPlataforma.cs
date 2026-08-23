namespace CaeManager.Application.Plataforma;

/// <summary>
/// ¿Puede este usuario ejecutar esta operación de plataforma <b>sobre este
/// alcance</b>?
///
/// <para>
/// Lo que comprueba, y nada más: <c>usuario ∧ capacidad AdminPlataforma ∧ el
/// alcance de la concesión cubre el objetivo ∧ vigente</c>. Ni
/// <c>EsPlataforma</c>, ni <c>ITenantActual</c>, ni "existe una concesión".
/// </para>
///
/// <para>
/// <b>Dos métodos y no uno con <c>Guid?</c>.</b> Un parámetro opcional
/// convertiría la autoridad más amplia en la que se obtiene <i>por omisión</i>:
/// quien olvidara pasar el tenant no recibiría un error, recibiría permiso
/// global. Así el llamante tiene que declarar qué autoridad necesita.
/// </para>
///
/// <para>
/// <b>La asimetría es parte del contrato:</b>
/// </para>
/// <code>
/// concesión global   →  satisface las dos
/// concesión acotada  →  satisface PuedeSobreTenant(sus tenants)
///                       y NUNCA PuedeGlobalmente
/// </code>
/// <para>
/// Nunca se puede derivar <c>PuedeGlobalmente = true</c> a partir de
/// <c>PuedeSobreTenant = true</c>. Sin esa regla, "tengo AdminPlataforma sobre
/// un cliente" se convertiría en "puedo listar el estado comercial de todos".
/// </para>
///
/// <para>
/// <b>Esto es ejercer una capacidad, no adquirirla.</b> No pide 2FA ni sesión
/// privilegiada: esos controles custodian la creación de autoridad (A2) y la
/// apertura de sesiones (A0). Exigir un segundo factor en cada operación
/// comercial ordinaria confundiría las dos fronteras.
/// </para>
/// </summary>
public interface IAutorizacionAdminPlataforma
{
    /// <param name="tenantObjetivoId">El tenant sobre el que se va a actuar.</param>
    Task<bool> PuedeSobreTenantAsync(
        Guid usuarioId, Guid tenantObjetivoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Para operaciones transversales por naturaleza: listar el estado comercial
    /// de todos los tenants, o dar de alta uno que <b>todavía no existe</b> y al
    /// que por tanto no hay nada que acotar.
    /// </summary>
    Task<bool> PuedeGlobalmenteAsync(Guid usuarioId, CancellationToken cancellationToken = default);
}
