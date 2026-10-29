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

    /// <summary>
    /// Listados cuya página resuelve la empresa gestionada activa con <c>ContextoEmpresaActiva.ResolverAsync</c>
    /// y, en el estado 4a (<c>contexto.SinSeleccion</c>, que compone las mismas dos condiciones que
    /// <c>ClientesAutorizados.PideElegirEmpresa</c>), enseña «Selecciona una empresa» en vez de la lista.
    /// </summary>
    public static TheoryData<string, string> ListadosConEstado4a => new()
    {
        { "Trabajadores", "/trabajadores/exportar.xlsx" },
        { "Documentos", "/documentos/exportar.xlsx" },
        { "Vehiculos", "/vehiculos/exportar.xlsx" },
        { "Proyectos", "/proyectos/exportar.xlsx" },
        { "Empresas", "/empresas/exportar.xlsx" },
        { "Subcontratas", "/subcontratas/exportar.xlsx" },
        { "Clientes", "/clientes/exportar.xlsx" },
        { "Centros", "/centros/exportar.xlsx" },
    };

    /// <summary>
    /// Quien exporta un listado es quien lo ve, también en el estado 4a: si la página no enseña los datos
    /// del Tenant de origen a quien tiene que elegir empresa, su exportación los niega antes de consultar
    /// o generar nada (OWASP API1, ASVS V4.2.1), y redirige a la propia página.
    /// </summary>
    [Theory]
    [MemberData(nameof(ListadosConEstado4a))]
    public void La_exportacion_de_un_listado_con_estado_4a_pide_elegir_empresa_antes_de_consultar(string listado, string ruta)
    {
        var carpeta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features", listado);
        var pagina = File.ReadAllText(Path.Combine(carpeta, "Pages", $"{listado}.razor.cs"));
        var endpoint = File.ReadAllText(Path.Combine(carpeta, $"{listado}Endpoints.cs"));

        pagina.Should().Contain("ContextoEmpresaActiva.ResolverAsync(")
            .And.Contain("_sinEmpresaSeleccionada = contexto.SinSeleccion;",
                $"control positivo: {listado}.razor enseña «Selecciona una empresa» en el estado 4a");
        endpoint.Should().Contain($"MapGet(\"{ruta}\", ExportarAsync)",
            $"{ruta} registra un método con nombre, el que miden los tests de comportamiento de Web.Tests");

        var cuerpo = endpoint[endpoint.IndexOf("Task<IResult> ExportarAsync(", StringComparison.Ordinal)..];
        var guarda = cuerpo.IndexOf("ClientesAutorizados.PideElegirEmpresa(autorizados, tenantActual.TenantId)", StringComparison.Ordinal);
        var redireccion = cuerpo.IndexOf($"return Results.Redirect(\"/{listado.ToLowerInvariant()}\");", StringComparison.Ordinal);
        var consulta = cuerpo.IndexOf("PaginadorExportacion.PaginarAsync(", StringComparison.Ordinal);

        guarda.Should().BeGreaterThanOrEqualTo(0,
            $"{ruta} no exporta los datos del Tenant de origen a quien tiene que elegir empresa, como {listado}.razor");
        redireccion.Should().BeGreaterThan(guarda, $"{ruta} devuelve a {listado}.razor, que pide elegir empresa");
        consulta.Should().BeGreaterThan(redireccion, $"{ruta} niega antes de consultar el listado");
    }

    /// <summary>
    /// Toda página que resuelve el estado 4a y tiene exportación está en <see cref="ListadosConEstado4a"/>:
    /// una pantalla nueva con «Selecciona una empresa» no puede dejar su exportación fuera de la guarda.
    ///
    /// <para>
    /// Límite del instrumento: solo ve listados con la forma <c>Features/X/Pages/X.razor.cs</c> más
    /// <c>Features/X/XEndpoints.cs</c>. Una pantalla que resuelva el estado 4a desde un componente o
    /// con otro nombre de fichero, o cuyo endpoint viva en otro fichero, queda fuera de esta búsqueda.
    /// </para>
    /// </summary>
    [Fact]
    public void Todo_listado_con_estado_4a_y_exportacion_esta_en_la_teoria()
    {
        var features = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features");
        var conEstado4aYExportacion = Directory.GetDirectories(features)
            .Select(Path.GetFileName)
            .Where(listado =>
            {
                var pagina = Path.Combine(features, listado!, "Pages", $"{listado}.razor.cs");
                var endpoint = Path.Combine(features, listado!, $"{listado}Endpoints.cs");
                return File.Exists(pagina) && File.Exists(endpoint)
                    && File.ReadAllText(pagina).Contains("ContextoEmpresaActiva.ResolverAsync(", StringComparison.Ordinal)
                    && File.ReadAllText(endpoint).Contains("/exportar.xlsx\"", StringComparison.Ordinal);
            })
            .ToList();

        conEstado4aYExportacion.Should().HaveCountGreaterThanOrEqualTo(8, "control positivo: hoy son ocho listados");
        conEstado4aYExportacion.Should().BeEquivalentTo(ListadosConEstado4a.Select(fila => (string)fila[0]));
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
    }
}
