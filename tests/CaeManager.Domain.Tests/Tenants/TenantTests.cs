using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Tenants;

public class TenantTests
{
    [Fact]
    public void Crea_un_tenant_activo_por_defecto()
    {
        var tenant = new Tenant("GESEME");

        tenant.Nombre.Should().Be("GESEME");
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
        var tenant = new Tenant("  KHS  ");

        tenant.Nombre.Should().Be("KHS");
    }

    [Fact]
    public void Suspender_cambia_el_estado_a_suspendido()
    {
        var tenant = new Tenant("KHS");

        tenant.Suspender();

        tenant.Estado.Should().Be(EstadoTenant.Suspendido);
    }

    [Fact]
    public void Reactivar_revierte_la_suspension()
    {
        var tenant = new Tenant("KHS");
        tenant.Suspender();

        tenant.Reactivar();

        tenant.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public void RenombrarA_actualiza_el_nombre()
    {
        var tenant = new Tenant("KHS");

        tenant.RenombrarA("KHS Group");

        tenant.Nombre.Should().Be("KHS Group");
    }

    [Fact]
    public void RenombrarA_no_permite_un_nombre_vacio()
    {
        var tenant = new Tenant("KHS");

        var accion = () => tenant.RenombrarA("");

        accion.Should().Throw<ArgumentException>();
        tenant.Nombre.Should().Be("KHS");
    }

    [Fact]
    public void Un_tenant_nuevo_no_tiene_TenantId_propio()
    {
        // Tenant extiende Entity directamente, no EntidadConTenant — no debe
        // exponer ningún TenantId (ver docs/MULTITENANCY.md § 4.1).
        typeof(Tenant).Should().NotBeAssignableTo<CaeManager.Domain.Common.EntidadConTenant>();
    }
}
