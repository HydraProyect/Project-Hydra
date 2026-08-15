using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class ConocimientoDeteccionCampoTests
{
    [Fact]
    public void Constructor_asigna_los_campos_recibidos()
    {
        var conocimiento = new ConocimientoDeteccionCampo("cif", FuenteDatoPlantilla.EmpresaCif, 100);

        conocimiento.EtiquetaNormalizada.Should().Be("cif");
        conocimiento.FuenteDatoCandidata.Should().Be(FuenteDatoPlantilla.EmpresaCif);
        conocimiento.Prioridad.Should().Be(100);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rechaza_etiqueta_vacia(string etiqueta)
    {
        var accion = () => new ConocimientoDeteccionCampo(etiqueta, FuenteDatoPlantilla.EmpresaCif, 100);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_etiqueta_demasiado_larga()
    {
        var etiqueta = new string('a', ConocimientoDeteccionCampo.LongitudMaximaEtiquetaNormalizada + 1);

        var accion = () => new ConocimientoDeteccionCampo(etiqueta, FuenteDatoPlantilla.EmpresaCif, 100);

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(FuenteDatoPlantilla.Manual)]
    [InlineData(FuenteDatoPlantilla.Constante)]
    public void Constructor_rechaza_fuentes_que_la_deteccion_no_puede_sugerir(FuenteDatoPlantilla fuente)
    {
        var accion = () => new ConocimientoDeteccionCampo("cif", fuente, 100);

        accion.Should().Throw<ArgumentException>();
    }
}
