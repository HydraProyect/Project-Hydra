using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Comercial.Common;

/// <summary>
/// El tenant de plataforma nunca es él mismo un suscriptor (ver
/// TenantConfiguration.HasData) — mismo criterio que
/// RegistrarSuscripcionTenantCommand y ObtenerEstadoComercialTenantsQuery.
/// Vive aquí, en Application, porque UsosDeEsPlataformaCongeladosTests
/// congela a cero las apariciones del flag en el ensamblado de
/// CaeManager.Web. El webhook de Stripe aplica este criterio en
/// <see cref="ProcesadorWebhookSuscripcion"/> y la resincronización en su
/// Command, los dos a través de este método.
/// </summary>
public static class TenantComercialExtensions
{
    public static bool EsSuscribible(this Tenant tenant) => !tenant.EsPlataforma;
}
