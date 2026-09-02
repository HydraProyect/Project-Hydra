using CaeManager.Domain.Importacion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Importacion;

public class HistorialImportacionTests
{
    [Fact]
    public void Exito_rechaza_una_plantilla_vacia()
    {
        var accion = () => HistorialImportacion.Exito(
            "", "archivo.xlsx", Guid.NewGuid(), 1, 0, 0);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exito_acepta_una_plantilla_de_exactamente_50_caracteres()
    {
        var plantillaDe50 = new string('P', 50);

        var historial = HistorialImportacion.Exito(
            plantillaDe50, "archivo.xlsx", Guid.NewGuid(), 1, 0, 0);

        historial.Plantilla.Should().Be(plantillaDe50);
    }

    // HasMaxLength(50) en HistorialImportacionConfiguration — este límite se
    // valida en el dominio para fallar rápido en vez de dejar que EF Core
    // descubra el límite con un 22001 de Postgres al guardar (ver el bug real
    // que motivó esta prueba: Importacion.razor.cs guardaba el Titulo largo de
    // UI de la plantilla "Combinada" —54 caracteres— en vez de un label corto).
    [Fact]
    public void Exito_rechaza_una_plantilla_de_51_caracteres()
    {
        var plantillaDe51 = new string('P', 51);

        var accion = () => HistorialImportacion.Exito(
            plantillaDe51, "archivo.xlsx", Guid.NewGuid(), 1, 0, 0);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Fallo_rechaza_una_plantilla_de_51_caracteres()
    {
        var plantillaDe51 = new string('P', 51);

        var accion = () => HistorialImportacion.Fallo(
            plantillaDe51, "archivo.xlsx", Guid.NewGuid(), "mensaje de error");

        accion.Should().Throw<ArgumentException>();
    }
}
