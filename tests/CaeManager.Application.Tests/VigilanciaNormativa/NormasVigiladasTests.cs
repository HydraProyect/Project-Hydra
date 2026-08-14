using CaeManager.Application.VigilanciaNormativa;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.VigilanciaNormativa;

public class NormasVigiladasTests
{
    [Theory]
    [InlineData("Ley 31/1995, de 8 de noviembre, de Prevención de Riesgos Laborales.", "LPRL")]
    [InlineData("Modificación de la Ley de Prevención de Riesgos Laborales.", "LPRL")]
    [InlineData("Real Decreto 39/1997, por el que se aprueba el Reglamento de los Servicios de Prevención.", "RSP")]
    [InlineData("Real Decreto 171/2004, de coordinación de actividades empresariales.", "RD 171/2004")]
    [InlineData("Modificación del Real Decreto 1215/1997, sobre equipos de trabajo.", "RD 1215/1997")]
    [InlineData("Real Decreto 773/1997, sobre equipos de protección individual.", "RD 773/1997")]
    public void Detecta_una_norma_vigilada_por_el_titulo(string titulo, string normaEsperada)
    {
        var resultado = NormasVigiladas.Detectar(titulo);

        resultado.Should().Be(normaEsperada);
    }

    [Fact]
    public void La_deteccion_no_distingue_mayusculas_de_minusculas()
    {
        var resultado = NormasVigiladas.Detectar("REAL DECRETO 171/2004, texto en mayúsculas.");

        resultado.Should().Be("RD 171/2004");
    }

    [Theory]
    [InlineData("Resolución de 6 de agosto de 2026, de la Subsecretaría, sobre convocatoria de libre designación.")]
    [InlineData("Real Decreto 1027/2011, sobre otra materia sin relación.")]
    public void No_detecta_ninguna_norma_en_publicaciones_sin_relacion(string titulo)
    {
        var resultado = NormasVigiladas.Detectar(titulo);

        resultado.Should().BeNull();
    }
}
