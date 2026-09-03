using CaeManager.Infrastructure.Importacion;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.IntegrationTests.Importacion;

/// <summary>
/// Invariante «nada se descarta en silencio» (IMPORTACION.md § 3 bis, DCR-12
/// B) sobre <see cref="ClosedXmlPlantillaClientesService.AnalizarAsync"/> —
/// auditada por REC-129 junto con los otros dos analizadores de plantilla.
/// Este servicio, a diferencia del analizador de referencia, no tiene
/// ninguna celda de fecha: su única rama silenciosa es la fila de ejemplo.
/// </summary>
public class ClosedXmlPlantillaClientesServiceTests
{
    [Fact]
    public async Task Fila_de_ejemplo_no_genera_ninguna_entrada_ni_se_importa()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "EJEMPLO — Borra esta fila antes de importar";
        hoja.Cell(2, 2).Value = "N";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        plan.ClientesCentros.Should().BeEmpty();
    }

    [Fact]
    public async Task Fila_valida_se_importa_como_ClienteCentro()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "Cliente Norte S.A.";
        hoja.Cell(2, 2).Value = "C";
        hoja.Cell(2, 3).Value = "Calle Norte 1";
        hoja.Cell(2, 4).Value = "Ana García";

        var plan = await AnalizarAsync(libro);

        plan.Omitidos.Should().BeEmpty();
        var clienteCentro = plan.ClientesCentros.Should().ContainSingle().Subject;
        clienteCentro.Nombre.Should().Be("Cliente Norte S.A.");
        clienteCentro.EsCritico.Should().BeTrue();
    }

    [Fact]
    public async Task Nombre_duplicado_dentro_del_archivo_queda_omitido_con_su_motivo()
    {
        var libro = NuevoLibroBase();
        var hoja = libro.Worksheets.Worksheet("Clientes");
        hoja.Cell(2, 1).Value = "Cliente Norte S.A.";
        hoja.Cell(3, 1).Value = "Cliente Norte S.A.";

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Fila.Should().Be(3);
        omitido.Motivo.Should().Be("Nombre duplicado dentro del propio archivo.");
        plan.ClientesCentros.Should().ContainSingle();
    }

    [Fact]
    public async Task Hoja_ausente_queda_omitida_completa()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("OtraHoja"); // Sin la hoja "Clientes" — ClosedXML exige al menos una hoja para guardar.

        var plan = await AnalizarAsync(libro);

        var omitido = plan.Omitidos.Should().ContainSingle().Subject;
        omitido.Hoja.Should().Be("Clientes");
        omitido.Motivo.Should().Be("No se encontró la hoja \"Clientes\" en el archivo.");
        plan.ClientesCentros.Should().BeEmpty();
    }

    private static XLWorkbook NuevoLibroBase()
    {
        var libro = new XLWorkbook();
        libro.Worksheets.Add("Clientes");
        return libro;
    }

    private static async Task<CaeManager.Application.Importacion.PlanImportacionDto> AnalizarAsync(XLWorkbook libro)
    {
        var servicio = new ClosedXmlPlantillaClientesService(new CentrosQueryContextFalso(), new EmpresasQueryContextFalso());

        using var flujo = new MemoryStream();
        libro.SaveAs(flujo);
        flujo.Position = 0;
        return await servicio.AnalizarAsync(flujo);
    }
}
