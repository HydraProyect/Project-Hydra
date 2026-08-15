using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Tenants;

public class TenantTests
{
    [Fact]
    public void Crea_un_tenant_activo_por_defecto()
    {
        var tenant = new Tenant("ArcoSPA");

        tenant.Nombre.Should().Be("ArcoSPA");
        tenant.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_permite_crear_un_tenant_sin_nombre(string nombreInvalido)
    {
        var accion = () => new Tenant(nombreInvalido);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_un_nombre_mayor_a_la_longitud_maxima()
    {
        var nombreDemasiadoLargo = new string('a', Tenant.LongitudMaximaNombre + 1);

        var accion = () => new Tenant(nombreDemasiadoLargo);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Recorta_espacios_en_blanco_del_nombre()
    {
        var tenant = new Tenant("  Ibertec  ");

        tenant.Nombre.Should().Be("Ibertec");
    }

    [Fact]
    public void Suspender_cambia_el_estado_a_suspendido()
    {
        var tenant = new Tenant("Ibertec");

        tenant.Suspender();

        tenant.Estado.Should().Be(EstadoTenant.Suspendido);
    }

    [Fact]
    public void Reactivar_revierte_la_suspension()
    {
        var tenant = new Tenant("Ibertec");
        tenant.Suspender();

        tenant.Reactivar();

        tenant.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public void RenombrarA_actualiza_el_nombre()
    {
        var tenant = new Tenant("Ibertec");

        tenant.RenombrarA("Ibertec Group");

        tenant.Nombre.Should().Be("Ibertec Group");
    }

    [Fact]
    public void RenombrarA_no_permite_un_nombre_vacio()
    {
        var tenant = new Tenant("Ibertec");

        var accion = () => tenant.RenombrarA("");

        accion.Should().Throw<ArgumentException>();
        tenant.Nombre.Should().Be("Ibertec");
    }

    [Fact]
    public void Un_tenant_nuevo_no_tiene_TenantId_propio()
    {
        // Tenant extiende Entity directamente, no EntidadConTenant — no debe
        // exponer ningún TenantId (ver docs/MULTITENANCY.md § 4.1).
        typeof(Tenant).Should().NotBeAssignableTo<CaeManager.Domain.Common.EntidadConTenant>();
    }

    [Fact]
    public void El_perfil_de_vocabulario_por_defecto_es_cliente_directo()
    {
        var tenant = new Tenant("Ibertec");

        tenant.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.ClienteDirecto);
    }

    [Fact]
    public void Se_puede_declarar_el_perfil_consultora_al_crear()
    {
        var tenant = new Tenant("ArcoSPA", PerfilVocabularioTenant.Consultora);

        tenant.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.Consultora);
    }

    [Fact]
    public void CambiarPerfilVocabulario_corrige_el_perfil_declarado()
    {
        var tenant = new Tenant("Ibertec", PerfilVocabularioTenant.ClienteDirecto);

        tenant.CambiarPerfilVocabulario(PerfilVocabularioTenant.Consultora);

        tenant.PerfilVocabulario.Should().Be(PerfilVocabularioTenant.Consultora);
    }

    // Horizonte 1.7 ("Billing mínimo viable") — EstadoComercial es un campo
    // aparte de Estado (ver EstadoComercialTenant): estos tests son la
    // contrapartida de Suspender_cambia_el_estado_a_suspendido/Reactivar_revierte_la_suspension
    // de arriba, demostrando que son independientes entre sí.

    [Fact]
    public void Un_tenant_nuevo_no_tiene_suscripcion_vinculada()
    {
        var tenant = new Tenant("Ibertec");

        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.SinSuscripcion);
        tenant.StripeCustomerId.Should().BeNull();
        tenant.StripeSubscriptionId.Should().BeNull();
    }

    [Fact]
    public void VincularSuscripcionStripe_registra_los_ids_y_el_estado_inicial()
    {
        var tenant = new Tenant("Ibertec");

        tenant.VincularSuscripcionStripe("cus_123", "sub_123", EstadoComercialTenant.Activa);

        tenant.StripeCustomerId.Should().Be("cus_123");
        tenant.StripeSubscriptionId.Should().Be("sub_123");
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.Activa);
        tenant.EstadoComercialActualizadoEnUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VincularSuscripcionStripe_no_permite_un_customer_id_vacio(string invalido)
    {
        var tenant = new Tenant("Ibertec");

        var accion = () => tenant.VincularSuscripcionStripe(invalido, "sub_123", EstadoComercialTenant.Activa);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void VincularSuscripcionStripe_no_permite_un_subscription_id_vacio(string invalido)
    {
        var tenant = new Tenant("Ibertec");

        var accion = () => tenant.VincularSuscripcionStripe("cus_123", invalido, EstadoComercialTenant.Activa);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActualizarEstadoComercial_no_permite_cambiar_el_estado_sin_suscripcion_vinculada()
    {
        var tenant = new Tenant("Ibertec");

        var accion = () => tenant.ActualizarEstadoComercial(EstadoComercialTenant.SoloLectura);

        accion.Should().Throw<InvalidOperationException>();
        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.SinSuscripcion);
    }

    [Fact]
    public void ActualizarEstadoComercial_aplica_el_nuevo_estado_de_una_suscripcion_ya_vinculada()
    {
        var tenant = new Tenant("Ibertec");
        tenant.VincularSuscripcionStripe("cus_123", "sub_123", EstadoComercialTenant.Activa);

        tenant.ActualizarEstadoComercial(EstadoComercialTenant.SoloLectura);

        tenant.EstadoComercial.Should().Be(EstadoComercialTenant.SoloLectura);
    }
}
