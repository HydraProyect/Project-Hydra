using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Integraciones;

public class CredencialIntegracionTests
{
    [Fact]
    public void Se_crea_con_el_refresh_token_informado()
    {
        var credencial = new CredencialIntegracion(Guid.NewGuid(), "token-inicial");

        credencial.RefreshToken.Should().Be("token-inicial");
    }

    [Fact]
    public void Rechaza_una_conexion_vacia()
    {
        var accion = () => new CredencialIntegracion(Guid.Empty, "token");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Rechaza_un_refresh_token_vacio()
    {
        var accion = () => new CredencialIntegracion(Guid.NewGuid(), " ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ActualizarRefreshToken_reemplaza_el_token_entero()
    {
        var credencial = new CredencialIntegracion(Guid.NewGuid(), "token-viejo");

        credencial.ActualizarRefreshToken("token-nuevo");

        credencial.RefreshToken.Should().Be("token-nuevo");
    }
}
