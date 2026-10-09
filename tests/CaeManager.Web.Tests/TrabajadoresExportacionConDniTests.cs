using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Trabajadores;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Decisión D2 del propietario (2026-10-08), que sustituye a la S4 del
/// 2026-09-24: <c>/trabajadores/exportar.xlsx</c> lleva el DNI del Trabajador,
/// el mismo que pinta el listado. Se mide el xlsx real que escribe
/// <see cref="TrabajadoresEndpoints.GenerarLibroAsync"/>, leído de vuelta con
/// ClosedXML. El rastro de la descarga lo mide
/// <see cref="TrabajadoresExportacionEstado4aTests"/>.
///
/// <para>
/// Límite del instrumento: no pasa por el routing de ASP.NET (el proyecto no
/// expone WebApplicationFactory). Que el endpoint delegue en
/// <c>GenerarLibroAsync</c> se ve en <c>TrabajadoresEndpoints.cs</c>; no lo
/// vigila este test.
/// </para>
/// </summary>
public class TrabajadoresExportacionConDniTests
{
    private const string DniMarcador = "01234567Z";
    private const string DniMarcadorOtro = "X1234567L";

    private static readonly TrabajadorListaDto[] Trabajadores =
    [
        new(Guid.NewGuid(), "Lucía", "Prieto Ramos", DniMarcador, "Refrigeración Norte, S.L.", EstadoDocumento.Vigente),
        new(Guid.NewGuid(), "Iker", "Sanz Olmo", DniMarcadorOtro, "Montajes Sur, S.L.", EstadoDocumento.Vencido),
    ];

    [Fact]
    public async Task La_cabecera_tiene_la_columna_de_DNI()
    {
        var celdas = await LeerHojaAsync();

        celdas[0].Should().Equal("Apellidos", "Nombre", "DNI", "Empresa/Subcontrata", "Documentación");
    }

    [Fact]
    public async Task Cada_fila_lleva_el_DNI_de_su_Trabajador()
    {
        var celdas = await LeerHojaAsync();

        celdas.Should().HaveCount(1 + Trabajadores.Length);

        celdas[1].Should().Equal("Prieto Ramos", "Lucía", DniMarcador, "Refrigeración Norte, S.L.", celdas[1][4]);
        celdas[2][2].Should().Be(DniMarcadorOtro);
    }

    [Fact]
    public async Task Un_Trabajador_sin_DNI_deja_la_celda_vacia()
    {
        TrabajadorListaDto[] sinDni = [new(Guid.NewGuid(), "Ana", "Gil Soto", null, "Montajes Sur, S.L.")];

        await using var stream = await TrabajadoresEndpoints.GenerarLibroAsync(sinDni.ToAsyncEnumerable());
        using var libro = new XLWorkbook(stream);

        libro.Worksheet("Trabajadores").Cell(2, 3).GetString().Should().BeEmpty();
        libro.Worksheet("Trabajadores").Cell(2, 4).GetString().Should().Be("Montajes Sur, S.L.");
    }

    private static async Task<List<List<string>>> LeerHojaAsync()
    {
        await using var stream = await TrabajadoresEndpoints.GenerarLibroAsync(Trabajadores.ToAsyncEnumerable());
        using var libro = new XLWorkbook(stream);
        var hoja = libro.Worksheet("Trabajadores");
        return hoja.RangeUsed()!.Rows()
            .Select(fila => fila.Cells().Select(c => c.GetString()).ToList())
            .ToList();
    }
}
