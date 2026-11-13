using System.Text.Json;
using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// <c>/api/v1</c> devuelve <see cref="ResultadoPaginado{T}"/> tal cual (Minimal API, <c>System.Text.Json</c> con los valores web
/// por defecto). <see cref="ResultadoPaginado{T}.FiltroRevisionIaAplicado"/> es un dato interno entre el handler del listado de
/// Documentos y quien lo pagina (la pantalla y la exportación), no parte de ese contrato: no puede aparecer en el JSON, ni a
/// <c>true</c> ni a <c>false</c>.
/// </summary>
public class ResultadoPaginadoFiltroRevisionIaTests
{
    private static readonly JsonSerializerOptions OpcionesWeb = new(JsonSerializerDefaults.Web);

    [Fact]
    public void El_filtro_de_revision_IA_aplicado_no_viaja_en_el_JSON_de_la_API()
    {
        var resultado = new ResultadoPaginado<string>(["Aptitud médica"], 1, 1, 20) { FiltroRevisionIaAplicado = true };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(resultado, OpcionesWeb));

        var propiedades = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        propiedades.Should().Contain(["elementos", "totalElementos"],
            "control positivo: el envoltorio se serializa con sus propiedades de siempre");
        propiedades.Should().NotContain(p => p.Contains("revisionIa", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sin_el_filtro_tampoco_aparece_la_propiedad()
    {
        var json = JsonSerializer.Serialize(new ResultadoPaginado<string>([], 0, 1, 20), OpcionesWeb);

        json.Should().NotContainEquivalentOf("filtroRevisionIaAplicado");
    }
}
