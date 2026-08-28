using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// F4.2a: el alta/edición de un usuario de portal (rol Cliente,
/// <c>ApplicationUser.ClienteId</c>) se vincula por CIF a una <c>Empresa</c>
/// (ver <c>BuscarEmpresaPorCifQuery</c>), nunca a la tabla legacy
/// <c>Clientes</c> — que ya no recibe altas desde F3b, así que resolver
/// contra ella deja sin poder vincularse a cualquier Cliente creado después
/// de esa congelación (ver ApplicationUser.ClienteId, doc-comment).
///
/// <para>
/// La propiedad real que protege este ratchet no es el nombre
/// <c>ClienteId</c> (que se conserva a propósito, ver doc-comment) ni el
/// nombre de una query concreta (<c>BuscarClientePorCifQuery</c>, retirada en
/// este mismo incremento) — es que ningún archivo de la feature Usuarios
/// vuelva a depender de la fuente de datos legacy (<c>IClientesQueryContext</c>
/// / <c>dbContext.Clientes</c> / el tipo de dominio <c>Cliente</c>) para
/// resolver esa vinculación, sea cual sea el nombre que le pongan.
/// </para>
///
/// <para>
/// <b>F3c (2026-08-28)</b>: <c>IClientesQueryContext</c>, el tipo
/// <c>Cliente</c> y la tabla <c>Clientes</c> ya no existen. El patrón que
/// este ratchet persigue no puede volver a aparecer sin reintroducir antes
/// todo eso, así que su sensibilidad es residual — se deja escrito para que
/// nadie lo lea como una barrera viva que sigue atrapando algo.
/// </para>
/// </summary>
public class VinculacionUsuarioClienteNoUsaClientesLegacyTests
{
    private static readonly Regex DependenciaDeClientesLegacy = new(
        @"IClientesQueryContext|dbContext\s*\.\s*Clientes\b|CaeManager\.Domain\.Clientes\.Cliente\b",
        RegexOptions.Compiled);

    [Fact]
    public void La_feature_Usuarios_no_depende_de_la_fuente_de_datos_legacy_Clientes()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", "CaeManager.Web", "Features", "Usuarios");

        Directory.Exists(directorio).Should().BeTrue($"se esperaba encontrar {directorio}");

        var infractores = Directory
            .EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => DependenciaDeClientesLegacy.IsMatch(File.ReadAllText(a)))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(r => r)
            .ToList();

        infractores.Should().BeEmpty(
            "desde F4.2a, vincular un usuario de portal Cliente a una contraparte se resuelve contra Empresa " +
            "(BuscarEmpresaPorCifQuery) — volver a leer la tabla legacy Clientes para esto reintroduce el gap " +
            "que impedía vincular cualquier cliente creado después de F3b");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
