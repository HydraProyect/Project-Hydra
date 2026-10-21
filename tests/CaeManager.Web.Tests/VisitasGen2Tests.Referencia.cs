using Bunit;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class VisitasGen2Tests
{
    [Fact]
    public void Las_referencias_distinguen_visitas_creadas_en_el_mismo_milisegundo()
    {
        var instante = new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);
        var primera = Guid.CreateVersion7(instante);
        var segunda = Guid.CreateVersion7(instante);
        primera.ToString("N")[..12].Should().Be(segunda.ToString("N")[..12]);
        var mediator = new MediatorVisitas
        {
            Visitas =
            {
                Visita("Centro Norte") with { Id = primera },
                Visita("Centro Sur") with { Id = segunda }
            }
        };

        var cut = Renderizar(mediator);

        var referenciaPrimera = "VIS-" + primera.ToString("N")[^8..].ToUpperInvariant();
        var referenciaSegunda = "VIS-" + segunda.ToString("N")[^8..].ToUpperInvariant();
        referenciaPrimera.Should().NotBe(referenciaSegunda);
        Fila(cut, "Centro Norte").TextContent.Should().Contain(referenciaPrimera);
        Fila(cut, "Centro Sur").TextContent.Should().Contain(referenciaSegunda);
    }

    [Theory]
    [InlineData("01a16015-1234-7000-8000-0000abcdef12", "VIS-ABCDEF12")]
    [InlineData("01a16015-1234-7000-8000-000000000001", "VIS-00000001")]
    public void La_referencia_es_estable_y_conserva_el_formato(string id, string referencia)
    {
        var mediator = new MediatorVisitas
        {
            Visitas = { Visita("Centro Norte") with { Id = Guid.Parse(id) } }
        };
        var cut = Renderizar(mediator);

        Fila(cut, "Centro Norte").TextContent.Should().Contain(referencia);
        cut.Render();
        Fila(cut, "Centro Norte").TextContent.Should().Contain(referencia);
    }
}
