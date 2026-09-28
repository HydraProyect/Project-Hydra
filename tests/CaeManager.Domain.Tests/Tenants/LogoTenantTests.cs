using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Tenants;

/// <summary>
/// Logo del Tenant (contrato del selector de Tenant, lote 1, invariante I12): la clave del blob tiene
/// que ser un PNG de la carpeta del propio Tenant, <c>{Id:N}/{guid}.png</c>, la que genera
/// <c>IFileStorageService.GuardarAsync</c>. Un blob, un propietario.
/// </summary>
public class LogoTenantTests
{
    private const string Version = "0123456789abcdef";
    private static readonly DateTime Ahora = new(2026, 9, 28, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Acepta_un_png_de_su_propia_carpeta()
    {
        var tenant = new Tenant("Tenant propietario");
        var clave = $"{tenant.Id:N}/{Guid.NewGuid():N}.png";

        tenant.EstablecerLogo(clave, Version, Ahora);

        tenant.LogoArchivoClave.Should().Be(clave);
        tenant.LogoVersion.Should().Be(Version);
        tenant.LogoActualizadoEnUtc.Should().Be(Ahora);
    }

    [Fact]
    public void Rechaza_la_clave_de_otro_Tenant()
    {
        var tenant = new Tenant("Tenant propietario");
        var otro = new Tenant("Otro Tenant");

        var accion = () => tenant.EstablecerLogo($"{otro.Id:N}/{Guid.NewGuid():N}.png", Version, Ahora);

        accion.Should().Throw<ArgumentException>();
        tenant.LogoArchivoClave.Should().BeNull();
    }

    [Theory]
    [InlineData("{0}/{1}.svg")]
    [InlineData("{0}/{1}.jpg")]
    [InlineData("{0}/logo/{1}.png")]
    [InlineData(@"{0}/..\{1}.png")]
    [InlineData("{0}/.png")]
    [InlineData("{1}.png")]
    public void Rechaza_claves_que_no_son_un_png_directo_de_su_carpeta(string formato)
    {
        var tenant = new Tenant("Tenant propietario");
        var clave = string.Format(formato, tenant.Id.ToString("N"), Guid.NewGuid().ToString("N"));

        var accion = () => tenant.EstablecerLogo(clave, Version, Ahora);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("corta")]
    [InlineData("0123456789abcdef0")]
    public void Rechaza_una_version_que_no_mide_lo_declarado(string version)
    {
        var tenant = new Tenant("Tenant propietario");

        var accion = () => tenant.EstablecerLogo($"{tenant.Id:N}/{Guid.NewGuid():N}.png", version, Ahora);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Retirar_deja_el_Tenant_sin_logo()
    {
        var tenant = new Tenant("Tenant propietario");
        tenant.EstablecerLogo($"{tenant.Id:N}/{Guid.NewGuid():N}.png", Version, Ahora);

        tenant.RetirarLogo(Ahora.AddHours(1));

        tenant.LogoArchivoClave.Should().BeNull();
        tenant.LogoVersion.Should().BeNull();
        tenant.LogoActualizadoEnUtc.Should().Be(Ahora.AddHours(1));
    }
}
