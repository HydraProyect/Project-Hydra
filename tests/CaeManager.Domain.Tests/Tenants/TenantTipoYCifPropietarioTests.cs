using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Tenants;

public class TenantTipoYCifPropietarioTests
{
    [Fact]
    public void Un_Tenant_nuevo_nace_sin_tipo()
    {
        new Tenant("Organización").TipoTenant.Should().BeNull();
    }

    [Fact]
    public void Tenant_no_expone_el_CIF_del_Tenant_propietario()
    {
        typeof(Tenant).GetProperties().Select(p => p.Name).Should().NotContain(n => n.Contains("Cif"),
            "la tabla Tenants la leen los tres roles de la aplicación; el CIF vive en CifPropietarioTenant (DA-11)");
    }

    [Fact]
    public void Declarar_el_tipo_lo_guarda()
    {
        var tenant = new Tenant("Organización");

        tenant.DeclararTipo(TipoTenant.GestionPropia);

        tenant.TipoTenant.Should().Be(TipoTenant.GestionPropia);
    }

    [Fact]
    public void El_tipo_no_se_vuelve_a_declarar()
    {
        var tenant = new Tenant("Organización");
        tenant.DeclararTipo(TipoTenant.GestionPropia);

        var accion = () => tenant.DeclararTipo(TipoTenant.OperadorCaeExterno);

        accion.Should().Throw<InvalidOperationException>();
        tenant.TipoTenant.Should().Be(TipoTenant.GestionPropia);
    }

    [Fact]
    public void Un_tipo_fuera_del_enumerado_se_rechaza()
    {
        var tenant = new Tenant("Organización");

        var accion = () => tenant.DeclararTipo((TipoTenant)99);

        accion.Should().Throw<ArgumentOutOfRangeException>();
        tenant.TipoTenant.Should().BeNull();
    }

    [Fact]
    public void Declarar_el_CIF_lo_guarda_normalizado_para_su_Tenant()
    {
        var tenantId = Guid.NewGuid();

        var cif = CifPropietarioTenant.Declarar(tenantId, " b-1234567.4 ");

        cif.TenantId.Should().Be(tenantId);
        cif.Cif.Should().Be("B12345674");
    }

    [Theory]
    [InlineData("B12345675")]
    [InlineData("ABC123456")]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_CIF_invalido_se_rechaza(string cif)
    {
        var accion = () => CifPropietarioTenant.Declarar(Guid.NewGuid(), cif);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void El_NIF_de_una_persona_fisica_es_valido()
    {
        CifPropietarioTenant.Declarar(Guid.NewGuid(), "12345678z").Cif.Should().Be("12345678Z");
    }

    [Fact]
    public void Sin_Tenant_no_hay_CIF()
    {
        var accion = () => CifPropietarioTenant.Declarar(Guid.Empty, "B12345674");

        accion.Should().Throw<ArgumentException>();
    }
}
