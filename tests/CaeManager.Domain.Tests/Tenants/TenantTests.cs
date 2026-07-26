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
}
