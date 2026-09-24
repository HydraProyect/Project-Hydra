using CaeManager.Application.Documentos.Commands.CrearDocumento;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Documentos;

/// <summary>
/// «No caduca» es una confirmación expresa del Gestor CAE y excluye una fecha
/// de vencimiento: los dos validadores rechazan la combinación antes de que
/// llegue a <c>CalculadoraEstadoDocumento.ResolverVigencia</c>.
/// </summary>
public class VigenciaEnComandosDeDocumentoValidatorTests
{
    private static readonly DateOnly Emision = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-10);
    private static readonly DateOnly Vencimiento = Emision.AddYears(1);

    private static CrearDocumentoCommand Crear(DateOnly? vencimiento, bool noCaduca) =>
        new(Guid.NewGuid(), null, null, null, null, Guid.NewGuid(), Emision, vencimiento, null, null, noCaduca);

    private static RenovarDocumentoCommand Renovar(DateOnly? vencimiento, bool noCaduca) =>
        new(Guid.NewGuid(), Emision, vencimiento, null, null, NoCaduca: noCaduca);

    [Fact]
    public void Crear_rechaza_fecha_de_vencimiento_y_no_caduca_a_la_vez()
    {
        new CrearDocumentoCommandValidator().Validate(Crear(Vencimiento, noCaduca: true)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Renovar_rechaza_fecha_de_vencimiento_y_no_caduca_a_la_vez()
    {
        new RenovarDocumentoCommandValidator().Validate(Renovar(Vencimiento, noCaduca: true)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Crear_admite_fecha_o_no_caduca_o_ninguna(bool conFecha, bool noCaduca)
    {
        new CrearDocumentoCommandValidator().Validate(Crear(conFecha ? Vencimiento : null, noCaduca)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Renovar_admite_fecha_o_no_caduca_o_ninguna(bool conFecha, bool noCaduca)
    {
        new RenovarDocumentoCommandValidator().Validate(Renovar(conFecha ? Vencimiento : null, noCaduca)).IsValid.Should().BeTrue();
    }
}
