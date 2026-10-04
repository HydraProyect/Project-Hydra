using CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// La periodicidad especial de un Centro (meses) admite de 1 a 120 o vacía (2026-10-04). El mismo tope lo aplican el dominio
/// (<c>TipoDocumentoCentro</c>) y un CHECK en base de datos; este es el que da el mensaje claro a la pantalla.
/// </summary>
public class EstablecerDocumentacionRequeridaCentroPeriodicidadValidatorTests
{
    private static EstablecerDocumentacionRequeridaCentroCommand Con(int? meses) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Incluido: true, meses, BloqueaAcceso: false, null, null, null);

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(12)]
    [InlineData(120)]
    public void Acepta_vacia_o_de_1_a_120_meses(int? meses) =>
        new EstablecerDocumentacionRequeridaCentroCommandValidator().Validate(Con(meses)).IsValid.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(121)]
    [InlineData(100000)]
    public void Rechaza_fuera_de_1_a_120_meses_con_un_mensaje_que_dice_el_rango(int meses)
    {
        var resultado = new EstablecerDocumentacionRequeridaCentroCommandValidator().Validate(Con(meses));

        resultado.IsValid.Should().BeFalse();
        resultado.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Contain("entre 1 y 120");
    }
}
