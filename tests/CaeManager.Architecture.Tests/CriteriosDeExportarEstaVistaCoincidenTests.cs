using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// «Exportar esta vista» (decisión D2 del 2026-10-08) solo exporta lo que se ve si la página y su
/// endpoint hablan de los mismos criterios: la página los nombra en <c>CriteriosExportar</c> y el
/// endpoint los recibe como parámetros de consulta. Un criterio que la página envía con un nombre
/// que el endpoint no lee se ignora en silencio y el fichero sale con filas de más; uno que el
/// endpoint lee y la página no envía es un filtro de la pantalla que la exportación no respeta.
///
/// <para>
/// Límite del instrumento: compara NOMBRES en el texto fuente. No ve si el endpoint, una vez
/// recibido el criterio, lo pasa a la consulta: eso lo miden los tests del endpoint de
/// Trabajadores y los E2E de exportación (estos últimos, solo para la búsqueda).
/// </para>
/// </summary>
public class CriteriosDeExportarEstaVistaCoincidenTests
{
    public static TheoryData<string, string> Listados => new()
    {
        { "Trabajadores", "/trabajadores/exportar.xlsx" },
        { "Empresas", "/empresas/exportar.xlsx" },
        { "Clientes", "/clientes/exportar.xlsx" },
        { "Centros", "/centros/exportar.xlsx" },
        { "Subcontratas", "/subcontratas/exportar.xlsx" },
        { "Documentos", "/documentos/exportar.xlsx" },
    };

    [Theory]
    [MemberData(nameof(Listados))]
    public void La_pagina_envia_exactamente_los_criterios_que_su_endpoint_lee(string listado, string ruta)
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features", listado);
        var pagina = File.ReadAllText(Path.Combine(carpeta, "Pages", $"{listado}.razor.cs"));
        var endpoint = File.ReadAllText(Path.Combine(carpeta, $"{listado}Endpoints.cs"));

        var enviados = CriteriosDeLaPagina(pagina);
        var leidos = ParametrosDeConsulta(endpoint, ruta);

        enviados.Should().NotBeEmpty("toda página con exportación declara CriteriosExportar");
        enviados.Should().Contain("q", "control positivo: los seis listados tienen buscador");
        enviados.Should().BeEquivalentTo(leidos,
            $"«Exportar esta vista» de {listado} debe llevar a {ruta} los mismos criterios que el endpoint lee");
    }

    /// <summary>Las claves <c>["nombre"] =</c> del inicializador de <c>CriteriosExportar</c>.</summary>
    private static List<string> CriteriosDeLaPagina(string fuente)
    {
        var bloque = Regex.Match(fuente, @"CriteriosExportar\s*=>\s*new\(\)\s*\{(?<cuerpo>.*?)\};", RegexOptions.Singleline);
        return Regex.Matches(bloque.Groups["cuerpo"].Value, @"\[""(?<nombre>\w+)""\]\s*=")
            .Select(m => m.Groups["nombre"].Value).ToList();
    }

    /// <summary>
    /// Los parámetros opcionales (<c>string? x = null</c>, <c>bool x = false</c>) del manejador de la
    /// ruta: los que ASP.NET enlaza desde la consulta. Con <c>[FromQuery(Name = "…")]</c>, ese nombre.
    /// </summary>
    private static List<string> ParametrosDeConsulta(string fuente, string ruta)
    {
        // Trabajadores registra un método con nombre; los demás, una lambda en el propio MapGet.
        var desde = fuente.Contains($"MapGet(\"{ruta}\", ExportarAsync)", StringComparison.Ordinal)
            ? fuente.IndexOf("Task<IResult> ExportarAsync(", StringComparison.Ordinal)
            : fuente.IndexOf($"MapGet(\"{ruta}\"", StringComparison.Ordinal);
        desde.Should().BeGreaterThanOrEqualTo(0, $"el endpoint {ruta} existe");

        var firma = fuente[desde..fuente.IndexOf('{', desde)];
        return Regex.Matches(firma, @"(?:\[[\w.]*FromQuery\(Name\s*=\s*""(?<alias>\w+)""\)\]\s*)?(?:string\?|bool)\s+(?<nombre>\w+)\s*=\s*(?:null|false)")
            .Select(m => m.Groups["alias"].Success ? m.Groups["alias"].Value : m.Groups["nombre"].Value).ToList();
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
    }
}
