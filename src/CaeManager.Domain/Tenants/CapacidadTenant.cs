namespace CaeManager.Domain.Tenants;

/// <summary>
/// Capacidad que el Administrador del Tenant propietario concede a una cuenta de su propio Tenant, además de su
/// rol. Es plano de Propiedad: no es una capacidad de plataforma (<c>CapacidadPrivilegio</c>, plano 3) ni una
/// Asignación de Operación o de Cartera (plano 2), y nunca alcanza a un usuario de un Operador CAE externo
/// (decisión D-3 del 2026-11-15). Se guarda como texto.
/// </summary>
public enum CapacidadTenant
{
    /// <summary>
    /// Tramitar y resolver las solicitudes de derechos del interesado (RGPD arts. 15 a 22) del Tenant
    /// propietario. Registrar una solicitud no la necesita.
    /// </summary>
    DerechosDelInteresado = 1,
}
