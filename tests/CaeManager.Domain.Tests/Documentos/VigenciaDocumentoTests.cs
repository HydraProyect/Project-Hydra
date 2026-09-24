using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

/// <summary>
/// Mismo contrato que <see cref="VigenciaEnPlataformaTests"/>: los dos «sin
/// fecha» son valores distintos y una fila incoherente no se interpreta.
/// </summary>
public class VigenciaDocumentoTests
{
    private static readonly DateOnly Fecha = new(2026, 7, 15);

    [Fact]
    public void Sin_confirmar_no_es_lo_mismo_que_no_caduca()
    {
        VigenciaDocumento.SinConfirmar.Should().NotBe(VigenciaDocumento.NoCaduca);
        VigenciaDocumento.SinConfirmar.FechaVencimiento.Should().BeNull();
        VigenciaDocumento.NoCaduca.FechaVencimiento.Should().BeNull();
    }

    [Fact]
    public void El_valor_por_defecto_es_sin_confirmar()
    {
        default(VigenciaDocumento).Should().Be(VigenciaDocumento.SinConfirmar);
    }

    [Fact]
    public void Desde_una_fecha_opcional_sin_fecha_queda_sin_confirmar_y_nunca_no_caduca()
    {
        VigenciaDocumento.DesdeFechaOpcional(null).Should().Be(VigenciaDocumento.SinConfirmar);
        VigenciaDocumento.DesdeFechaOpcional(Fecha).Should().Be(VigenciaDocumento.VenceEl(Fecha));
    }

    [Fact]
    public void Vencer_en_una_fecha_conserva_la_fecha()
    {
        var vigencia = VigenciaDocumento.VenceEl(Fecha);

        vigencia.Estado.Should().Be(EstadoVigenciaDocumento.VenceEnFecha);
        vigencia.FechaVencimiento.Should().Be(Fecha);
    }

    [Fact]
    public void Rehidratar_rechaza_vence_en_fecha_sin_fecha()
    {
        var rehidratar = () => VigenciaDocumento.Rehidratar(EstadoVigenciaDocumento.VenceEnFecha, null);

        rehidratar.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData(EstadoVigenciaDocumento.SinConfirmar)]
    [InlineData(EstadoVigenciaDocumento.NoCaduca)]
    public void Rehidratar_rechaza_una_fecha_que_no_deberia_estar(EstadoVigenciaDocumento estado)
    {
        var rehidratar = () => VigenciaDocumento.Rehidratar(estado, Fecha);

        rehidratar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Rehidratar_rechaza_un_ordinal_desconocido()
    {
        var rehidratar = () => VigenciaDocumento.Rehidratar((EstadoVigenciaDocumento)99, null);

        rehidratar.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Rehidratar_devuelve_lo_mismo_que_se_guardo()
    {
        foreach (var vigencia in new[] { VigenciaDocumento.SinConfirmar, VigenciaDocumento.NoCaduca, VigenciaDocumento.VenceEl(Fecha) })
            VigenciaDocumento.Rehidratar(vigencia.Estado, vigencia.FechaVencimiento).Should().Be(vigencia);
    }
}
