using CaeManager.Domain.AltaAutonoma;
using FluentAssertions;

namespace CaeManager.Domain.Tests.AltaAutonoma;

public class TramosTrabajadoresActivosTests
{
    [Theory]
    [InlineData(1, TramoTrabajadoresActivos.De1A10)]
    [InlineData(10, TramoTrabajadoresActivos.De1A10)]
    [InlineData(11, TramoTrabajadoresActivos.De11A40)]
    [InlineData(40, TramoTrabajadoresActivos.De11A40)]
    [InlineData(41, TramoTrabajadoresActivos.De41A80)]
    [InlineData(80, TramoTrabajadoresActivos.De41A80)]
    [InlineData(81, TramoTrabajadoresActivos.De81A150)]
    [InlineData(150, TramoTrabajadoresActivos.De81A150)]
    [InlineData(151, TramoTrabajadoresActivos.De151A300)]
    [InlineData(300, TramoTrabajadoresActivos.De151A300)]
    public void Cada_numero_de_Trabajadores_activos_cae_en_su_tramo(int trabajadores, TramoTrabajadoresActivos esperado)
    {
        TramosTrabajadoresActivos.ParaTrabajadoresActivos(trabajadores).Should().Be(esperado);

        var (minimo, maximo) = TramosTrabajadoresActivos.Rango(esperado);
        trabajadores.Should().BeInRange(minimo, maximo);
    }

    [Theory]
    [InlineData(301)]
    [InlineData(5000)]
    public void Desde_301_no_hay_tramo_se_cotiza(int trabajadores) =>
        TramosTrabajadoresActivos.ParaTrabajadoresActivos(trabajadores).Should().BeNull();

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Sin_Trabajadores_activos_no_hay_tramo_valido(int trabajadores)
    {
        var accion = () => TramosTrabajadoresActivos.ParaTrabajadoresActivos(trabajadores);
        accion.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Los_rangos_son_contiguos_y_cubren_de_1_a_300()
    {
        var siguiente = 1;
        foreach (var tramo in Enum.GetValues<TramoTrabajadoresActivos>())
        {
            var (minimo, maximo) = TramosTrabajadoresActivos.Rango(tramo);
            minimo.Should().Be(siguiente);
            siguiente = maximo + 1;
        }

        siguiente.Should().Be(TramosTrabajadoresActivos.MaximoConTramo + 1);
    }
}
