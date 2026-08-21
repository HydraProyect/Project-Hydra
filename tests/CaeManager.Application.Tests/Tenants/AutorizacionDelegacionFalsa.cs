using CaeManager.Application.Tenants;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Doble de <see cref="IAutorizacionDelegacionTenant"/>. Los tests de invariante
/// de dominio le piden que autorice, porque lo que ejercitan es lo que ocurre
/// DESPUÉS de la autorización; los tests de autorización usan el propio handler
/// con este doble diciendo que no.
/// </summary>
public class AutorizacionDelegacionFalsa : IAutorizacionDelegacionTenant
{
    private readonly bool autoriza;
    private readonly Guid? tenantDelAdministrador;

    public AutorizacionDelegacionFalsa(bool autoriza) => this.autoriza = autoriza;

    private AutorizacionDelegacionFalsa(Guid tenantDelAdministrador) =>
        this.tenantDelAdministrador = tenantDelAdministrador;

    /// <summary>
    /// Modela a un <c>Administrador</c> del tenant indicado. La implementación
    /// real exige rol <b>y</b> pertenencia, así que solo autoriza cuando le
    /// preguntan por SU tenant.
    ///
    /// <para>
    /// Es lo que hace distinguibles los dos casos que un doble booleano
    /// colapsaría: el Administrador del Cliente Delegante y el de la Consultora
    /// tienen el mismo rol y deben recibir respuestas distintas. Un handler que
    /// preguntara por <c>TenantConsultoraId</c> autorizaría al segundo, y con
    /// un doble de "sí o no" el test seguiría pasando.
    /// </para>
    /// </summary>
    public static AutorizacionDelegacionFalsa AdministradorDe(Guid tenant) => new(tenant);

    public Guid? UltimoTenantConsultado { get; private set; }

    public Task<bool> PuedeGestionarDelegacionesAsync(
        Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default)
    {
        UltimoTenantConsultado = tenantClienteDeleganteId;

        return Task.FromResult(tenantDelAdministrador is null
            ? autoriza
            : tenantDelAdministrador == tenantClienteDeleganteId);
    }
}
