using CaeManager.Domain.Tenants;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Tenants;

public class TenantTipoYCifPropietarioTests
{
    [Fact]
    public void Un_Tenant_nuevo_nace_sin_tipo_ni_CIF_propietario()
    {
        var tenant = new Tenant("Organización");

        tenant.TipoTenant.Should().BeNull();
        tenant.CifPropietario.Should().BeNull();
    }

    [Fact]
    public void Declarar_guarda_el_tipo_y_el_CIF_normalizado()
    {
        var tenant = new Tenant("Organización");

        tenant.DeclararTipoYCifPropietario(TipoTenant.GestionPropia, " b-1234567.4 ");

        tenant.TipoTenant.Should().Be(TipoTenant.GestionPropia);
        tenant.CifPropietario.Should().Be("B12345674");
    }

    [Theory]
    [InlineData("B12345675")]
    [InlineData("ABC123456")]
    [InlineData("")]
    public void Un_CIF_invalido_se_rechaza(string cif)
    {
        var tenant = new Tenant("Organización");

        var accion = () => tenant.DeclararTipoYCifPropietario(TipoTenant.GestionPropia, cif);

        accion.Should().Throw<ArgumentException>();
        tenant.CifPropietario.Should().BeNull();
        tenant.TipoTenant.Should().BeNull();
    }

    [Fact]
    public void Una_vez_declarados_no_se_vuelven_a_declarar()
    {
        var tenant = new Tenant("Organización");
        tenant.DeclararTipoYCifPropietario(TipoTenant.GestionPropia, "B12345674");

        var accion = () => tenant.DeclararTipoYCifPropietario(TipoTenant.OperadorCaeExterno, "12345678Z");

        accion.Should().Throw<InvalidOperationException>();
        tenant.CifPropietario.Should().Be("B12345674");
    }

    [Fact]
    public void Un_tipo_fuera_del_enumerado_se_rechaza()
    {
        var accion = () => new Tenant("Organización").DeclararTipoYCifPropietario((TipoTenant)99, "B12345674");

        accion.Should().Throw<ArgumentOutOfRangeException>();
    }
}
