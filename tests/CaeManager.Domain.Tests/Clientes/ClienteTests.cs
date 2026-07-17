using CaeManager.Domain.Clientes;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Clientes;

public class ClienteTests
{
    private const string CifValido = "B12345674";

    [Fact]
    public void Crea_un_cliente_valido()
    {
        var cliente = new Cliente("COBEGA (Coca-Cola European Partners)", CifValido, esCritico: true);

        cliente.RazonSocial.Should().Be("COBEGA (Coca-Cola European Partners)");
        cliente.Cif.Should().Be(CifValido);
        cliente.EsCritico.Should().BeTrue();
        cliente.EstaEliminado.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void No_permite_crear_un_cliente_sin_razon_social(string razonSocialInvalida)
    {
        var accion = () => new Cliente(razonSocialInvalida, CifValido, esCritico: false);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_una_razon_social_mayor_a_la_longitud_maxima()
    {
        var razonSocialDemasiadoLarga = new string('a', Cliente.LongitudMaximaRazonSocial + 1);

        var accion = () => new Cliente(razonSocialDemasiadoLarga, CifValido, esCritico: false);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("B12345670")] // formato de CIF, dígito de control incorrecto
    [InlineData("77189989A")] // formato de DNI, no de CIF de empresa
    public void No_permite_un_cif_invalido(string cifInvalido)
    {
        var accion = () => new Cliente("RENDELSUR", cifInvalido, esCritico: false);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Marcar_como_eliminado_es_idempotente()
    {
        var cliente = new Cliente("RENDELSUR", CifValido, esCritico: true);
        var usuarioId = Guid.NewGuid();

        cliente.MarcarComoEliminado(usuarioId);
        var fechaEliminacion = cliente.EliminadoEnUtc;
        cliente.MarcarComoEliminado(usuarioId);

        cliente.EstaEliminado.Should().BeTrue();
        cliente.EliminadoEnUtc.Should().Be(fechaEliminacion);
    }

    [Fact]
    public void Restaurar_revierte_la_eliminacion()
    {
        var cliente = new Cliente("RENDELSUR", CifValido, esCritico: true);
        cliente.MarcarComoEliminado(Guid.NewGuid());

        cliente.Restaurar();

        cliente.EstaEliminado.Should().BeFalse();
        cliente.EliminadoEnUtc.Should().BeNull();
    }
}
