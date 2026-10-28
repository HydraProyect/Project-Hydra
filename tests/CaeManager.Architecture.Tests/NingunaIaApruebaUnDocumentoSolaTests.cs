using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Decisión de Chris (hallazgo S-C-1, 2026-10-28): de momento ningún tipo de documento se
/// aprueba sin persona. Los valores <c>AprobacionDocumento.CrearAutomatica</c>,
/// <c>DecisionHumanaIa.AutomaticaSinRevision</c> y <c>DecisionValidacionOficial.AutoValidado</c>
/// siguen existiendo solo para leer registros históricos; ningún código de Application ni de
/// Infrastructure (salvo la siembra de datos de prueba) puede volver a producirlos. Reabrir esa
/// puerta es una decisión de producto y exige tocar esta lista en el mismo commit.
/// </summary>
public class NingunaIaApruebaUnDocumentoSolaTests
{
    private static readonly string[] Productores =
    [
        "CrearAutomatica(",
        "DecisionHumanaIa.AutomaticaSinRevision",
        "DecisionValidacionOficial.AutoValidado",
    ];

    [Fact]
    public void Application_e_Infrastructure_no_producen_aprobaciones_ni_validaciones_automaticas()
    {
        var raiz = RaizDelRepositorio();
        var infraestructura = Path.Combine(raiz, "src", "CaeManager.Infrastructure");
        var sembrado = Path.Combine(infraestructura, "Persistence", "Seed") + Path.DirectorySeparatorChar;

        var ficheros = new[] { Path.Combine(raiz, "src", "CaeManager.Application"), infraestructura }
            .SelectMany(d => Directory.EnumerateFiles(d, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !f.StartsWith(sembrado, StringComparison.OrdinalIgnoreCase));

        var infracciones = ficheros
            .Where(f => { var texto = File.ReadAllText(f); return Productores.Any(texto.Contains); })
            .Select(f => Path.GetRelativePath(raiz, f))
            .ToList();

        infracciones.Should().BeEmpty(
            "ningún documento se aprueba sin persona (S-C-1): la IA solo propone y un Gestor CAE confirma");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
