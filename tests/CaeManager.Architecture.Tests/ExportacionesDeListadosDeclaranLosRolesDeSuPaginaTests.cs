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

    /// <summary>
    /// Documentos y Trabajadores declaran <c>[Authorize]</c> sin roles (el rol Cliente entra con su
    /// alcance): su exportación no puede cerrarle lo que la página le abre. Lo que sí comparten con
    /// la página es el estado 4a: sin empresa elegida, redirigen al listado sin consultar.
    /// </summary>
    public static TheoryData<string, string> ListadosAbiertosATodoAutenticado => new()
    {
        { "Documentos", "/documentos/exportar.xlsx" },
        { "Trabajadores", "/trabajadores/exportar.xlsx" },
    };

    [Theory]
    [MemberData(nameof(ListadosAbiertosATodoAutenticado))]
    public void La_exportacion_de_un_listado_sin_roles_no_los_restringe_y_pide_elegir_empresa(string listado, string ruta)
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features", listado);
        var pagina = File.ReadAllText(Path.Combine(carpeta, "Pages", $"{listado}.razor"));
        var endpoint = File.ReadAllText(Path.Combine(carpeta, $"{listado}Endpoints.cs"));

        pagina.Should().MatchRegex(@"@attribute \[Authorize\]\r?\n",
            $"control positivo: {listado}.razor no restringe roles; si pasa a restringirlos, su exportación va a la teoría de arriba");
        endpoint.Should().Contain($"MapGet(\"{ruta}\", ExportarAsync);",
            $"{ruta} admite los mismos roles que {listado}.razor: todo usuario autenticado");
        endpoint.Should().NotContain($"MapGet(\"{ruta}\", ExportarAsync).SoloRolesDelListado()",
            $"{ruta} no puede cerrar al rol Cliente lo que {listado}.razor le abre");
        endpoint.Should().Contain("ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId)")
            .And.Contain($"return Results.Redirect(\"/{listado.ToLowerInvariant()}\");",
                $"{ruta} no exporta los datos del Tenant de origen a quien tiene que elegir empresa, como {listado}.razor");
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
    }
}
