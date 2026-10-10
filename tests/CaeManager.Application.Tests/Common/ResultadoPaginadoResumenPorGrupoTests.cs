using System.Text.Json;
using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// <c>/api/v1</c> devuelve <see cref="ResultadoPaginado{T}"/> tal cual (Minimal API, <c>System.Text.Json</c> con
/// los valores web por defecto). El resumen por grupo es un dato de presentación del listado agrupado de la
/// interfaz y no forma parte de ese contrato: no puede aparecer en el JSON, ni siquiera como <c>null</c>.
/// </summary>
public class ResultadoPaginadoResumenPorGrupoTests
{
    private static readonly JsonSerializerOptions OpcionesWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void El_resumen_por_grupo_no_viaja_en_el_JSON_de_la_API()
    {
        var resultado = new ResultadoPaginado<string>(["Centro Norte"], 1, 1, 20)
        {
            RecuentosPorEstado = new Dictionary<string, int> { ["Vigente"] = 1 },
            ResumenPorGrupo = new Dictionary<string, ResumenDeGrupo>
            {
                ["grupo"] = new(1, new Dictionary<string, int> { ["Vigente"] = 1 }),
            },
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(resultado, OpcionesWeb));

        var propiedades = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        propiedades.Should().Contain(["elementos", "totalElementos", "recuentosPorEstado"],
            "control positivo: el envoltorio se serializa con sus propiedades de siempre");
        propiedades.Should().NotContain(p => p.Contains("resumen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sin_resumen_tampoco_aparece_la_propiedad()
    {
        var json = JsonSerializer.Serialize(new ResultadoPaginado<string>([], 0, 1, 20), OpcionesWeb);

        json.Should().NotContainEquivalentOf("resumenPorGrupo");
    }
}
