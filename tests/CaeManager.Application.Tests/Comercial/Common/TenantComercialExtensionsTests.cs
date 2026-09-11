using CaeManager.Application.Comercial.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Common;

/// <summary>
/// Único punto de verdad de "tenant excluido de comercial", consumido por
/// ActualizarEstadoComercialTenantCommand y por WebhookStripeEndpoints — este
/// segundo no puede leer <c>Tenant.EsPlataforma</c> directamente
/// (<c>UsosDeEsPlataformaCongeladosTests.El_ensamblado_de_Web_no_depende_de_EsPlataforma</c>).
/// </summary>
public class TenantComercialExtensionsTests
{
    [Fact]
    public void El_tenant_de_plataforma_no_es_suscribible()
    {
        var tenant = new Tenant("TALVEG");
        tenant.MarcarComoPlataforma();

        tenant.EsSuscribible().Should().BeFalse();
    }

    [Fact]
    public void Un_tenant_cliente_si_es_suscribible()
    {
        var tenant = new Tenant("Ibertec");

        tenant.EsSuscribible().Should().BeTrue();
    }
}
