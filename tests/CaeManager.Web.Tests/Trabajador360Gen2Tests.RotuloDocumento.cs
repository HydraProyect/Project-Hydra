using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cabecera de Trabajador 360 según la maqueta del 20-10: el rótulo del documento de identidad es el tipo guardado
/// («DNI», «NIE» o «Pasaporte»), no un «DNI» fijo. Un valor heredado que no es un documento válido sigue rotulándose
/// «DNI»: no se le inventa un tipo.
/// </summary>
public partial class Trabajador360Gen2Tests
{
    [Theory]
    [InlineData("12345678Z", "DNI")]
    [InlineData("X1234567L", "NIE")]
    [InlineData("AB1234567", "Pasaporte")]
    [InlineData("X1234567A", "NIE")]
    [InlineData("SIN DNI", "DNI")]
    public void La_cabecera_rotula_el_documento_con_su_tipo(string documento, string rotulo)
    {
        var id = Guid.NewGuid();
        var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { Dni = documento };

        var cut = Renderizar(id);

        cut.Find(".trabajador360-meta > span").TextContent.Trim().Should().StartWith(rotulo)
            .And.Contain(documento);
    }

    [Theory]
    [InlineData("12345678Z", "Copiar el DNI: 12345678Z")]
    [InlineData("X1234567L", "Copiar el NIE: X1234567L")]
    [InlineData("AB1234567", "Copiar el pasaporte: AB1234567")]
    public void El_boton_de_copiar_nombra_el_mismo_tipo_que_el_rotulo(string documento, string nombreAccesible)
    {
        var id = Guid.NewGuid();
        var m = ConTrabajador(id);
        m.Detalles[id] = m.Detalles[id] with { Dni = documento };

        var cut = Renderizar(id);

        cut.Find(".trabajador360-meta > span button").GetAttribute("aria-label").Should().Be(nombreAccesible);
    }
}
