using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Quien exporta un listado es quien lo ve. Las páginas de Vehículos, Proyectos, Visitas y Gestiones
/// restringen roles con <c>[Authorize(Roles = …)]</c>; un endpoint mínimo solo tiene la política por
/// defecto (cualquier usuario autenticado, rol Cliente incluido), así que su exportación tiene que
/// declarar los mismos roles (<c>SoloRolesDelListado()</c>, que los nombra uno a uno).
///
/// <para>
/// Límite del instrumento: compara el texto fuente de la página, del endpoint y de la lista de
/// roles. Que ASP.NET deniegue de verdad a un rol de fuera lo miden los E2E de exportación.
/// </para>
/// </summary>
public class ExportacionesDeListadosDeclaranLosRolesDeSuPaginaTests
{
    public static TheoryData<string, string> Listados => new()
    {
        { "Vehiculos", "/vehiculos/exportar.xlsx" },
        { "Proyectos", "/proyectos/exportar.xlsx" },
        { "Visitas", "/visitas/exportar.xlsx" },
        { "Gestiones", "/gestiones/exportar.xlsx" },
    };

    [Theory]
    [MemberData(nameof(Listados))]
    public void La_exportacion_exige_los_mismos_roles_que_su_pagina(string listado, string ruta)
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features", listado);
        var pagina = File.ReadAllText(Path.Combine(carpeta, "Pages", $"{listado}.razor"));
        var endpoint = File.ReadAllText(Path.Combine(carpeta, $"{listado}Endpoints.cs"));
        var roles = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Exportacion", "RolesDeListados.cs"));

        var deLaPagina = Regex.Match(pagina, @"@attribute \[Authorize\(Roles = \$""(?<roles>[^""]+)""\)\]").Groups["roles"].Value;
        var rolesDeLaPagina = Regex.Matches(deLaPagina, @"Roles\.(?<rol>\w+)\}").Select(m => m.Groups["rol"].Value).ToList();
        var operativos = Regex.Match(roles, @"RequireRole\((?<roles>[^)]+)\)").Groups["roles"].Value;
        var rolesDeLaExportacion = Regex.Matches(operativos, @"Roles\.(?<rol>\w+)").Select(m => m.Groups["rol"].Value).ToList();

        rolesDeLaPagina.Should().NotBeEmpty($"control positivo: {listado}.razor restringe roles");
        rolesDeLaPagina.Should().NotContain("Cliente", "control positivo: el rol Cliente no entra en estas páginas");
        rolesDeLaExportacion.Should().BeEquivalentTo(rolesDeLaPagina,
            $"{ruta} no puede abrir a un rol lo que {listado}.razor le cierra, ni al revés");
        endpoint.Should().Contain($"MapGet(\"{ruta}\", ExportarAsync).SoloRolesDelListado()",
            $"{ruta} debe declarar los roles del listado: sin ellos lo descarga cualquier usuario autenticado");
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
    }
}
