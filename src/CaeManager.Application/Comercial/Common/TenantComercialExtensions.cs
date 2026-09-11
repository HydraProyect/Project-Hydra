using CaeManager.Domain.Tenants;

namespace CaeManager.Application.Comercial.Common;

/// <summary>
/// El tenant de plataforma nunca es él mismo un suscriptor (ver
/// TenantConfiguration.HasData) — mismo criterio que
/// RegistrarSuscripcionTenantCommand y ObtenerEstadoComercialTenantsQuery.
/// Vive aquí, en Application, porque UsosDeEsPlataformaCongeladosTests
/// congela a cero las apariciones del flag en el ensamblado de
/// CaeManager.Web: el webhook de Stripe necesita el mismo criterio sin
/// cruzar esa frontera, así que llama a este método en vez de leer la
/// propiedad directamente.
/// </summary>
public static class TenantComercialExtensions
{
    public static bool EsSuscribible(this Tenant tenant) => !tenant.EsPlataforma;
}
