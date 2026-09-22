using CaeManager.Application.Asignaciones.Queries.ObtenerAsignaciones;
using CaeManager.Web.Features.Asignaciones;
using CaeManager.Web.Features.Asignaciones.Recursos;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Tests;

/// <summary>
/// La exportación a Excel de asignaciones toma nombre de hoja, cabeceras y
/// estado de <see cref="TextosAsignaciones"/>. Las claves no coinciden con su
/// texto a propósito: si el localizador no encontrara el recurso devolvería el
/// nombre de la clave («ColumnaTrabajador»), y estos tests lo verían.
/// </summary>
public class AsignacionesExportacionTests
{
    private static IStringLocalizer<TextosAsignaciones> Textos() =>
        new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider()
            .GetRequiredService<IStringLocalizer<TextosAsignaciones>>();

    private static async IAsyncEnumerable<AsignacionListaDto> Asignaciones(params AsignacionListaDto[] asignaciones)
    {
        foreach (var asignacion in asignaciones)
        {
            await Task.Yield();
            yield return asignacion;
        }
    }

    private static AsignacionListaDto Asignacion(DateOnly? fechaBaja) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "Ana Pérez", Guid.NewGuid(), "Nave Norte", Guid.NewGuid(), "Refrielectric",
            new DateOnly(2026, 3, 2), fechaBaja);

    [Fact]
    public async Task Hoja_y_cabeceras_salen_de_los_recursos()
    {
        using var libro = await AsignacionesEndpoints.ConstruirLibroAsync(Asignaciones(), Textos());

        var hoja = libro.Worksheet(1);
        hoja.Name.Should().Be("Asignaciones");
        Enumerable.Range(1, 5).Select(c => hoja.Cell(1, c).GetString()).Should()
            .Equal("Trabajador", "Centro", "Cliente", "Fecha de alta", "Estado");
    }

    [Fact]
    public async Task El_estado_es_Activa_o_Baja_con_su_fecha()
    {
        using var libro = await AsignacionesEndpoints.ConstruirLibroAsync(
            Asignaciones(Asignacion(null), Asignacion(new DateOnly(2026, 9, 7))), Textos());

        var hoja = libro.Worksheet(1);
        hoja.Cell(2, 1).GetString().Should().Be("Ana Pérez");
        hoja.Cell(2, 5).GetString().Should().Be("Activa");
        hoja.Cell(3, 5).GetString().Should().Be("Baja el 07/09/2026");
    }
}
