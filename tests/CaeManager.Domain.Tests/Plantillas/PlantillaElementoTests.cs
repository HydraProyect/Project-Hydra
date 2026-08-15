using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class PlantillaElementoTests
{
    private static PlantillaElemento CrearElementoDeTexto() =>
        new(Guid.NewGuid(), TipoElementoPlantilla.Texto, 1, 100, 200, 150, 20, "Razón social",
            fuenteDato: FuenteDatoPlantilla.EmpresaRazonSocial);

    [Fact]
    public void Constructor_asigna_los_campos_recibidos()
    {
        var versionId = Guid.NewGuid();

        var elemento = new PlantillaElemento(
            versionId, TipoElementoPlantilla.Texto, 1, 10, 20, 100, 15, "CIF",
            fuenteDato: FuenteDatoPlantilla.EmpresaCif);

        elemento.PlantillaDocumentoVersionId.Should().Be(versionId);
        elemento.Tipo.Should().Be(TipoElementoPlantilla.Texto);
        elemento.EtiquetaVisible.Should().Be("CIF");
        elemento.FuenteDato.Should().Be(FuenteDatoPlantilla.EmpresaCif);
    }

    [Fact]
    public void Constructor_rechaza_version_vacia()
    {
        var accion = () => new PlantillaElemento(
            Guid.Empty, TipoElementoPlantilla.Texto, 1, 0, 0, 10, 10, "Campo");

        accion.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rechaza_pagina_menor_que_uno(int pagina)
    {
        var accion = () => new PlantillaElemento(
            Guid.NewGuid(), TipoElementoPlantilla.Texto, pagina, 0, 0, 10, 10, "Campo");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_ancho_o_alto_no_positivos()
    {
        var accion = () => new PlantillaElemento(
            Guid.NewGuid(), TipoElementoPlantilla.Texto, 1, 0, 0, 0, 10, "Campo");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_firma_sin_rol_de_firmante()
    {
        var accion = () => new PlantillaElemento(
            Guid.NewGuid(), TipoElementoPlantilla.Firma, 1, 0, 0, 200, 80, "Firma del trabajador");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_rol_de_firmante_en_un_elemento_que_no_es_firma()
    {
        var accion = () => new PlantillaElemento(
            Guid.NewGuid(), TipoElementoPlantilla.Texto, 1, 0, 0, 100, 20, "Campo",
            rolFirmante: RolFirmantePlantilla.Trabajador);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_acepta_firma_con_rol_de_firmante()
    {
        var elemento = new PlantillaElemento(
            Guid.NewGuid(), TipoElementoPlantilla.Firma, 1, 0, 0, 200, 80, "Firma del trabajador",
            rolFirmante: RolFirmantePlantilla.Trabajador);

        elemento.RolFirmante.Should().Be(RolFirmantePlantilla.Trabajador);
    }

    [Fact]
    public void Constructor_rechaza_fuente_constante_sin_valor()
    {
        var accion = () => new PlantillaElemento(
            Guid.NewGuid(), TipoElementoPlantilla.Constante, 1, 0, 0, 100, 20, "Campo",
            fuenteDato: FuenteDatoPlantilla.Constante);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reposicionar_actualiza_pagina_y_coordenadas()
    {
        var elemento = CrearElementoDeTexto();

        elemento.Reposicionar(2, 50, 60, 120, 25);

        elemento.Pagina.Should().Be(2);
        elemento.X.Should().Be(50);
        elemento.Y.Should().Be(60);
        elemento.Ancho.Should().Be(120);
        elemento.Alto.Should().Be(25);
    }

    [Fact]
    public void Reposicionar_rechaza_ancho_no_positivo()
    {
        var elemento = CrearElementoDeTexto();

        var accion = () => elemento.Reposicionar(1, 0, 0, 0, 20);

        accion.Should().Throw<ArgumentException>();
    }
}
