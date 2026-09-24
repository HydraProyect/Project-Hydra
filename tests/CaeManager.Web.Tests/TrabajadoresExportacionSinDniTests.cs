using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Trabajadores;
using ClosedXML.Excel;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Decisión S4 (2026-09-24): <c>/trabajadores/exportar.xlsx</c> no lleva el
/// DNI del Trabajador, ni en la cabecera ni en el contenido, aunque la query
/// del listado lo siga devolviendo. Se mide el xlsx real que escribe
/// <see cref="TrabajadoresEndpoints.GenerarLibroAsync"/>, leído de vuelta con
/// ClosedXML.
///
/// <para>
/// Límite del instrumento: no pasa por el routing de ASP.NET (el proyecto no
/// expone WebApplicationFactory). Que el endpoint delegue en
/// <c>GenerarLibroAsync</c> se ve en <c>TrabajadoresEndpoints.cs</c>; no lo
/// vigila este test.
/// </para>
/// </summary>
public class TrabajadoresExportacionSinDniTests
{
    private const string DniMarcador = "12345678Z";
    private const string DniMarcadorOtro = "X1234567L";

    private static readonly TrabajadorListaDto[] Trabajadores =
    [
        new(Guid.NewGuid(), "Lucía", "Prieto Ramos", DniMarcador, "Refrigeración Norte, S.L.", EstadoDocumento.Vigente),
        new(Guid.NewGuid(), "Iker", "Sanz Olmo", DniMarcadorOtro, "Montajes Sur, S.L.", EstadoDocumento.Vencido),
    ];

    [Fact]
    public async Task La_cabecera_no_tiene_columna_de_DNI()
    {
        var celdas = await LeerHojaAsync();

        celdas[0].Should().Equal("Apellidos", "Nombre", "Empresa/Subcontrata", "Documentación");
        celdas[0].Should().NotContain(c => c.Contains("DNI", StringComparison.OrdinalIgnoreCase)
                                        || c.Contains("NIE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ninguna_celda_contiene_el_DNI_de_un_Trabajador()
    {
        var celdas = await LeerHojaAsync();

        // Control positivo: el DTO de entrada SÍ lleva DNI y el libro SÍ
        // contiene las filas de esos Trabajadores. Sin esto, un libro vacío
        // o una entrada sin DNI dejarían el test en verde sin medir nada.
        Trabajadores.Should().OnlyContain(t => !string.IsNullOrEmpty(t.Dni));
        celdas.Should().HaveCount(1 + Trabajadores.Length);
        celdas.SelectMany(f => f).Should().Contain(["Prieto Ramos", "Sanz Olmo"]);

        celdas.SelectMany(f => f).Should().NotContain(c =>
            c.Contains(DniMarcador, StringComparison.OrdinalIgnoreCase) ||
            c.Contains(DniMarcadorOtro, StringComparison.OrdinalIgnoreCase));
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
