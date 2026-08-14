using CaeManager.Application.Comercial.Common;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comercial.Common;

public class MapeoEstadoComercialTenantTests
{
    [Theory]
    [InlineData(EstadoSuscripcionProveedor.Activa, EstadoComercialTenant.Activa)]
    [InlineData(EstadoSuscripcionProveedor.EnPeriodoGracia, EstadoComercialTenant.AvisoImpago)]
    [InlineData(EstadoSuscripcionProveedor.Impagada, EstadoComercialTenant.SoloLectura)]
    [InlineData(EstadoSuscripcionProveedor.Cancelada, EstadoComercialTenant.Suspendida)]
    public void Traduce_cada_estado_del_proveedor_a_la_etapa_del_gate_correspondiente(
        EstadoSuscripcionProveedor estadoProveedor, EstadoComercialTenant esperado)
    {
        MapeoEstadoComercialTenant.Desde(estadoProveedor).Should().Be(esperado);
    }

    [Fact]
    public void Un_estado_desconocido_no_se_traduce_a_ninguna_etapa()
    {
        // Null, no un valor por defecto arbitrario: "no tocar el estado
        // actual" es la respuesta segura ante un estado de Stripe que Hydra
        // no reconoce (ver el comentario de MapeoEstadoComercialTenant).
        MapeoEstadoComercialTenant.Desde(EstadoSuscripcionProveedor.Desconocida).Should().BeNull();
    }
}
