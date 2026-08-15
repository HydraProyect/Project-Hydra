using CaeManager.Domain.Documentos;
using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class PlantillaDocumentoTests
{
    private static PlantillaDocumento CrearPlantilla() =>
        new(OrigenPlantilla.Externa, "Ficha de acceso al centro", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual);

    [Fact]
    public void Nace_activa_y_sin_version_actual()
    {
        var plantilla = CrearPlantilla();

        plantilla.Estado.Should().Be(EstadoPlantillaDocumento.Activa);
        plantilla.VersionActualId.Should().BeNull();
    }

    [Fact]
    public void Constructor_rechaza_nombre_vacio()
    {
        var accion = () => new PlantillaDocumento(
            OrigenPlantilla.Externa, "  ", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EstablecerVersionActual_rechaza_id_vacio()
    {
        var plantilla = CrearPlantilla();

        var accion = () => plantilla.EstablecerVersionActual(Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void EstablecerVersionActual_asigna_la_version()
    {
        var plantilla = CrearPlantilla();
        var versionId = Guid.NewGuid();

        plantilla.EstablecerVersionActual(versionId);

        plantilla.VersionActualId.Should().Be(versionId);
    }

    [Fact]
    public void Archivar_y_Reactivar_cambian_el_estado()
    {
        var plantilla = CrearPlantilla();

        plantilla.Archivar();
        plantilla.Estado.Should().Be(EstadoPlantillaDocumento.Archivada);

        plantilla.Reactivar();
        plantilla.Estado.Should().Be(EstadoPlantillaDocumento.Activa);
    }
}
