using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

/// <summary>
/// <see cref="Centro.FijarNotaInterna"/>: la «Nota interna» de Centro 360, con el mismo contrato que la de Empresa 360
/// (mismo límite, vacía o solo espacios = sin nota) y sin tocar ningún otro dato del Centro.
/// </summary>
public class CentroNotaInternaTests
{
    private static Centro Nave() =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Nave de Cogullada", "C-07", "Calle de prueba 1", "Recepción", new DateOnly(2027, 1, 31));

    [Fact]
    public void Un_centro_nuevo_no_tiene_nota()
    {
        Nave().Notas.Should().BeNull();
    }

    [Fact]
    public void Fija_la_nota_recortada_y_conserva_los_saltos_de_linea()
    {
        var centro = Nave();

        centro.FijarNotaInterna("  Entrada por la puerta 3.\nPedir la llave en garita.  ");

        centro.Notas.Should().Be("Entrada por la puerta 3.\nPedir la llave en garita.");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData(null)]
    public void Vacia_o_solo_espacios_deja_el_centro_sin_nota(string? notas)
    {
        var centro = Nave();
        centro.FijarNotaInterna("Nota anterior.");

        centro.FijarNotaInterna(notas);

        centro.Notas.Should().BeNull();
    }

    [Fact]
    public void No_toca_ningun_otro_dato_del_centro()
    {
        var centro = Nave();

        centro.FijarNotaInterna("Nota.");

        centro.Nombre.Should().Be("Nave de Cogullada");
        centro.CodigoCentro.Should().Be("C-07");
        centro.Direccion.Should().Be("Calle de prueba 1");
        centro.Contacto.Should().Be("Recepción");
        centro.ContratoVigenteHasta.Should().Be(new DateOnly(2027, 1, 31));
    }

    [Fact]
    public void Admite_el_maximo_y_rechaza_uno_mas_sin_cambiar_la_nota()
    {
        var centro = Nave();
        centro.FijarNotaInterna(new string('a', Centro.LongitudMaximaNotas));
        centro.Notas.Should().HaveLength(Centro.LongitudMaximaNotas);

        var accion = () => centro.FijarNotaInterna(new string('b', Centro.LongitudMaximaNotas + 1));

        accion.Should().Throw<ArgumentException>()
            .WithMessage($"La nota interna no puede superar {Centro.LongitudMaximaNotas} caracteres.*");
        centro.Notas.Should().HaveLength(Centro.LongitudMaximaNotas).And.NotContain("b");
    }

    [Fact]
    public void El_limite_es_el_mismo_que_el_de_la_nota_de_Empresa()
    {
        Centro.LongitudMaximaNotas.Should().Be(CaeManager.Domain.Empresas.Empresa.LongitudMaximaNotas);
    }
}
