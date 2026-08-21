namespace CaeManager.Application.Tenants;

/// <summary>
/// ¿Puede este usuario crear o modificar las delegaciones <b>de este Cliente
/// Delegante</b>?
///
/// <para>
/// La autoridad no es de Hydra. ADR-004 § 12.2 la fija sin ambigüedad: la
/// vinculación entre dos tenants que ya existen es autoservicio descentralizado,
/// y <b>solo un usuario con rol <c>Administrador</c> en el tenant del Cliente
/// Delegante</b> aprueba, modifica o revoca la <c>DelegacionTenant</c>.
/// </para>
///
/// <para>
/// Y § 11.1 lo dice por el otro lado: <i>"Hydra nunca inicia una delegación.
/// Toda delegación la crea siempre un Tenant, una Consultora, o un
/// Administrador autorizado dentro del producto — nunca Hydra por sí sola"</i>.
/// Por eso esta autorización <b>no</b> consulta <c>EsPlataforma</c>: darle a la
/// plataforma la capacidad de vincular tenants arbitrarios sería exactamente lo
/// que ese apartado prohíbe. El único camino que sí es de plataforma —
/// <c>CrearClienteDeleganteCommand</c>— existe porque ahí el Cliente Delegante
/// <i>todavía no existe</i> y no hay otra parte que pueda consentir.
/// </para>
///
/// <para>
/// <b>El tenant importa tanto como el rol.</b> No basta con ser
/// <c>Administrador</c>: hay que serlo <b>del Cliente Delegante</b>. Un
/// Administrador de la Consultora tiene el mismo rol y ninguna autoridad aquí —
/// es la parte que recibe el acceso, no la que lo concede.
/// </para>
///
/// <para>
/// <b>Por qué no <c>[Authorize(Roles = "Administrador")]</c>.</b> Un atributo
/// sobre el claim responde "¿tiene ese rol?" y no "¿en qué tenant?", que es la
/// mitad que importa. Y desde F2b-2 el claim de rol ni siquiera es fiable para
/// estas decisiones: el middleware lo retira mientras hay sesión privilegiada,
/// así que la respuesta dependería de por dónde entró la petición.
/// </para>
/// </summary>
public interface IAutorizacionDelegacionTenant
{
    /// <param name="usuarioId">Quién pide crear o modificar la delegación.</param>
    /// <param name="tenantClienteDeleganteId">
    /// El tenant <b>propietario de los datos</b>, que es quien delega el acceso —
    /// nunca el de la Consultora, que es quien lo recibe.
    /// </param>
    Task<bool> PuedeGestionarDelegacionesAsync(
        Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default);
}
