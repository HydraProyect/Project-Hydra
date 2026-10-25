using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>DbSet.Find</c>/<c>FindAsync</c> y <c>DbSet.Local</c> leen del rastreador de EF sin pasar por el filtro
/// de Tenant ni por la RLS. En un circuito de Blazor Server el DbContext es Scoped y vive con el circuito:
/// tras retirar la selección de Tenant beneficiario, el rastreador conserva las entidades del Tenant
/// anterior y <c>Find</c> las devuelve (lo demuestra
/// <c>CambioDeTenantEnCalienteDelCircuitoBajoRuntimeTests</c>). Producción no usa <c>Find</c>; los
/// <c>.Local</c> que hay están catalogados con el motivo por el que no exponen datos de otro Tenant. Uno
/// nuevo hace fallar el test hasta que alguien lo justifique aquí.
/// </summary>
public class LecturasDelRastreadorDeEfEstanCatalogadasTests
{
    private static readonly Regex Find = new(@"\.Find(?:Async)?\s*\(", RegexOptions.Compiled);

    /// <summary><c>.Local</c> seguido de un acceso a miembro (en la misma línea o en la siguiente).</summary>
    private static readonly Regex Local = new(@"\.Local\s*(?:\.|\r?\n\s*\.)", RegexOptions.Compiled);

    private static readonly Dictionary<string, (int Usos, string Motivo)> LocalCatalogado = new()
    {
        ["src/CaeManager.Infrastructure/Persistence/Repositories/AlertaCriticaRepository.cs"] =
            (1, "deduplica la alerta añadida en la misma unidad de trabajo; el predicado exige el Tenant actual"),
        ["src/CaeManager.Infrastructure/Persistence/Repositories/RelacionEmpresarialRepository.cs"] =
            (1, "solo devuelve un booleano (alta pendiente del mismo par de Empresas, cuyos Ids son de un solo Tenant)"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/PilotoOutboundSeeder.cs"] = (1, "siembra, sin circuito"),
        ["src/CaeManager.Infrastructure/Persistence/Seed/CoberturaPantallasSeeder.cs"] = (2, "siembra, sin circuito"),
    };

    [Fact]
    public void Produccion_no_lee_con_Find()
    {
        var conFind = Recuento(Find);

        conFind.Should().BeEmpty(
            "Find sirve la entidad rastreada sin filtro de Tenant ni RLS; en un DbContext de circuito puede ser del "
            + "Tenant anterior. Se consulta con Where/FirstOrDefaultAsync, que sí pasan por los dos");
    }

    [Fact]
    public void Cada_lectura_de_Local_esta_catalogada()
    {
        var medido = Recuento(Local);

        medido.Should().ContainKey("src/CaeManager.Infrastructure/Persistence/Repositories/AlertaCriticaRepository.cs",
            "control positivo: el patrón encuentra los usos que ya existen");
        medido.Should().Equal(LocalCatalogado.ToDictionary(p => p.Key, p => p.Value.Usos),
            "un .Local nuevo lee del rastreador sin filtro de Tenant: se justifica aquí o se cambia por una consulta");
    }

    private static Dictionary<string, int> Recuento(Regex patron)
    {
        var raiz = RaizDelRepositorio();
        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            .Select(f => (Fichero: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Usos: patron.Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Usos > 0)
            .ToDictionary(x => x.Fichero, x => x.Usos);
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
    }
}
