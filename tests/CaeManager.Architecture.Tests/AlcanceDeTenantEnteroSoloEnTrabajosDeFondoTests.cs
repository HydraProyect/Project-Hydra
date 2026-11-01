using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Estos manejadores exponen <c>CalcularAsync</c> con el alcance como parámetro, y con <c>null</c>
/// devuelven el Tenant entero: solo sirve a un trabajo de fondo, sin usuario, ya acotado por
/// <c>AmbitoTenantExplicito</c>. Una sesión llega por MediatR a <c>Handle</c>, que aplica el alcance
/// del usuario. Instanciarlos a mano fuera de esos trabajos abriría esa vía a código interactivo.
/// </summary>
public class AlcanceDeTenantEnteroSoloEnTrabajosDeFondoTests
{
    private static readonly Regex InstanciacionManual = new(
        @"\bnew\s+Obtener(Visitas|SugerenciasVisitaCorreoPendientes|Alertas)QueryHandler\s*\(",
        RegexOptions.Compiled);

    private static readonly string[] TrabajosDeFondoAutorizados =
    [
        "src/CaeManager.Infrastructure/Alertas/EnvioAlertasVencimientoHostedService.cs",
        "src/CaeManager.Infrastructure/Visitas/VigilanciaVisitasUrgentesHostedService.cs",
    ];

    [Fact]
    public void Solo_los_trabajos_de_fondo_instancian_a_mano_estos_manejadores()
    {
        var raiz = RaizDelRepositorio();

        var quienesLosInstancian = Directory
            .EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => InstanciacionManual.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(raiz, f).Replace('\\', '/'))
            .ToList();

        // Igualdad, no subconjunto: si uno deja de instanciarlos, esta lista se queda vieja y el
        // control positivo del instrumento se perdería.
        quienesLosInstancian.Should().BeEquivalentTo(TrabajosDeFondoAutorizados);
    }

    private static string RaizDelRepositorio()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.GetFiles("*.slnx").Any() && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
