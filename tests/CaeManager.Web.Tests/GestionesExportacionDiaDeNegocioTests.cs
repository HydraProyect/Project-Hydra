using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Features.Gestiones;
using CaeManager.Web.Features.Gestiones.Recursos;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// «Exportar» de /gestiones saca en «Creada» el mismo día que pinta la columna del listado: el día de
/// negocio (España peninsular) del instante de creación, no su día UTC. Los casos están a un lado y
/// otro de la medianoche de Madrid, en horario de verano y de invierno.
/// </summary>
public class GestionesExportacionDiaDeNegocioTests
{
    [Theory]
    [InlineData("2026-07-15T22:30:00Z", 2026, 7, 16)] // 00:30 del 16 en Madrid (UTC+2)
    [InlineData("2026-07-15T21:30:00Z", 2026, 7, 15)] // 23:30 del 15 en Madrid
    [InlineData("2026-01-14T23:30:00Z", 2026, 1, 15)] // 00:30 del 15 en Madrid (UTC+1)
    [InlineData("2026-01-14T22:30:00Z", 2026, 1, 14)] // 23:30 del 14 en Madrid
    public async Task La_celda_Creada_lleva_el_dia_de_negocio_de_la_creacion(string creadoEnUtc, int ano, int mes, int dia)
    {
        var creada = DateTime.Parse(creadoEnUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var gestion = new GestionListaDto(
            Guid.NewGuid(), Guid.NewGuid(), "Lucía Prieto", Guid.NewGuid(), "Centro Norte",
            Guid.NewGuid(), "Formación PRL", EstadoGestion.Pendiente, creada);

        var filas = new List<IReadOnlyList<ClosedXML.Excel.XLCellValue>>();
        await foreach (var fila in GestionesEndpoints.FilasAsync(Una(gestion), new TextosEco()))
            filas.Add(fila);

        var celdaCreada = filas.Should().ContainSingle().Subject[^1];
        celdaCreada.IsDateTime.Should().BeTrue();
        celdaCreada.GetDateTime().Should().Be(new DateTime(ano, mes, dia));
    }

    private static async IAsyncEnumerable<GestionListaDto> Una(GestionListaDto gestion)
    {
        yield return gestion;
        await Task.CompletedTask;
    }

    /// <summary>Localizador que devuelve la clave: el texto del estado no es objeto de este test.</summary>
    private sealed class TextosEco : IStringLocalizer<TextosGestiones>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}
