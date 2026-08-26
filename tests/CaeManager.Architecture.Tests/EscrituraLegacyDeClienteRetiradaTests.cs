using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// F3b (D2, <c>f3b-decision-d2-transicion-acotada-2026-08-25.md</c> §2):
/// desde la congelación de Cliente, <c>Clientes</c> pasa a ser legacy
/// read-only — sigue existiendo y sigue siendo consultable (las seis
/// consultas de categoría C, sin tocar hasta F4), pero ningún código vuelve
/// a escribir en ella. <c>IClienteRepository</c>/<c>ClienteRepository</c>
/// se retiraron en el mismo commit que este ratchet: no quedan vivos "por si
/// acaso" una vez ningún comando los inyecta.
///
/// <para>
/// Sin este ratchet, "legacy read-only" es una promesa verbal. Con él,
/// cualquier PR futuro que reintroduzca un escritor (a mano, con
/// <c>CaeManagerDbContext</c> directo, o resucitando el repositorio
/// retirado) rompe CI de inmediato — no se descubre semanas después como
/// una divergencia silenciosa entre lo que el código escribe y lo que las
/// seis pantallas congeladas siguen leyendo.
/// </para>
/// </summary>
public class EscrituraLegacyDeClienteRetiradaTests
{
    /// <summary>
    /// Por la forma de la llamada y no por dónde vive: persigue cualquier
    /// <c>Add</c>/<c>Update</c>/<c>Remove</c> —y sus variantes de rango, más
    /// las de ejecución directa en SQL— sobre el DbSet <c>Clientes</c>, esté
    /// escrito como esté.
    /// </summary>
    private static readonly Regex EscrituraSobreClientes = new(
        @"\bClientes\s*\.\s*(Add|AddRange|Update|UpdateRange|Remove|RemoveRange|ExecuteDelete|ExecuteUpdate)\s*\(",
        RegexOptions.Compiled);

    [Theory]
    [InlineData("CaeManager.Application")]
    [InlineData("CaeManager.Infrastructure")]
    public void Ningun_codigo_de_produccion_escribe_en_el_DbSet_legacy_de_Clientes(string proyecto)
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", proyecto);

        var infractores = Directory
            .EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => EscrituraSobreClientes.IsMatch(File.ReadAllText(a)))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(r => r)
            .ToList();

        infractores.Should().BeEmpty(
            "desde la congelación de F3b, Cliente es una Empresa contraparte (Empresa.CrearComoCliente) y " +
            "toda escritura pasa por IEmpresaRepository — un escritor nuevo sobre el DbSet legacy Clientes " +
            "divergiría en silencio de las seis consultas que siguen leyéndolo hasta F4");
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
