using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class NormalizadorEtiquetaCampoTests
{
    [Theory]
    [InlineData("CIF", "cif")]
    [InlineData("Razón Social", "razon social")]
    [InlineData("  Dirección   del   Centro  ", "direccion del centro")]
    [InlineData("CIF:", "cif")]
    [InlineData("Nombre y Apellidos*", "nombre y apellidos")]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Normalizar_produce_la_forma_canonica_esperada(string? entrada, string esperado) =>
        NormalizadorEtiquetaCampo.Normalizar(entrada).Should().Be(esperado);
}
