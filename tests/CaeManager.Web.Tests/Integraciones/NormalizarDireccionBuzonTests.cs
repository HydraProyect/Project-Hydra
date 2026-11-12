using CaeManager.Domain.Integraciones;
using CaeManager.Web.Features.Integraciones.Endpoints;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>La dirección del buzón compartido llega por la URL de inicio del flujo OAuth: solo pasa una dirección limpia.</summary>
public class NormalizarDireccionBuzonTests
{
    [Theory]
    [InlineData("piloto@tenant.com", "piloto@tenant.com")]
    [InlineData("  piloto@tenant.com  ", "piloto@tenant.com")]
    public void Acepta_una_direccion_y_le_quita_los_espacios(string valor, string esperado) =>
        ConectarMicrosoft365Endpoints.NormalizarDireccionBuzon(valor).Should().Be(esperado);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-es-un-correo")]
    [InlineData("Piloto <piloto@tenant.com>")]
    [InlineData("piloto@tenant.com, otro@tenant.com")]
    public void Rechaza_lo_que_no_es_solo_una_direccion(string valor) =>
        ConectarMicrosoft365Endpoints.NormalizarDireccionBuzon(valor).Should().BeNull();

    [Fact]
    public void Rechaza_una_direccion_mas_larga_que_la_columna()
    {
        var direccion = new string('a', ConexionIntegracion.LongitudMaximaBuzonEmail) + "@tenant.com";

        ConectarMicrosoft365Endpoints.NormalizarDireccionBuzon(direccion).Should().BeNull();
    }
}
