using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El origen <c>SiembraDeDemostracion</c> del Encargo de administración (decisión de Chris,
/// 2026-10-16, opción A) es el único sin quien lo registra: dice «no lo registró nadie».
/// Sigue siendo verdad solo mientras ese encargo lo escriba la siembra del piloto y ningún
/// comando, endpoint o servicio del producto. Este trinquete vigila las dos puertas: la
/// fábrica de dominio <c>EncargoAdministracion.SembrarDeDemostracion</c> y el propio valor
/// del enum. <c>EncargoAdministracion.Registrar</c> ya rechaza ese origen en el dominio, y
/// la base exige que el origen y la ausencia de registrante vayan juntos
/// (<c>CK_EncargosAdministracion_RegistranteSegunOrigen</c>). Mismo instrumento que
/// <see cref="PilotoOutboundSoloDesdeElModoCliTests"/>: el texto de <c>src/</c>.
/// </summary>
public class EncargoDeSiembraSoloDesdeLaSiembraDelPilotoTests
{
    private const string Fabrica = "EncargoAdministracion.SembrarDeDemostracion(";
    private const string Origen = "OrigenEncargoAdministracion.SiembraDeDemostracion";

    [Fact]
    public void Solo_la_siembra_del_piloto_llama_a_la_fabrica_del_encargo_sin_registrante()
    {
        var llamadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains(Fabrica, StringComparison.Ordinal))
            .Select(Relativa)
            .ToList();

        llamadores.Should().BeEquivalentTo(
            ["src/CaeManager.Infrastructure/Persistence/Seed/PilotoOutboundSeeder.cs"],
            "un comando o un servicio que la llamara registraría encargos reales sin nadie que responda de ellos");
    }

    [Fact]
    public void Application_no_nombra_el_origen_de_la_siembra()
    {
        var quienesLoNombran = FicherosDeCodigo()
            .Where(f => Texto(f).Contains(Origen, StringComparison.Ordinal))
            .Select(Relativa)
            .ToList();

        quienesLoNombran.Should().Contain(
            "src/CaeManager.Web/Features/EncargoDeAdministracion/EncargoAdministracionPanel.razor.cs",
            "control positivo: el instrumento ve la pantalla que lo muestra");
        quienesLoNombran.Should().NotContain(f => f.StartsWith("src/CaeManager.Application/", StringComparison.Ordinal),
            "Application no produce ese origen ni decide nada con él: un comando que lo pasara a la fábrica general " +
            "fallaría en el dominio, y uno que lo comparara para decidir autoridad le daría un significado que no tiene");
    }

    private static string Texto(string fichero) => File.ReadAllText(fichero);

    private static string Relativa(string fichero) => Path.GetRelativePath(RaizDelRepositorio(), fichero).Replace('\\', '/');

    private static IEnumerable<string> FicherosDeCodigo() =>
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}"));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
