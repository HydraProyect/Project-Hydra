using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La siembra administrativa de la demo a dirección reutiliza
/// <c>EscenariosDireccionDemoSeeder.SembrarRamaAsync</c> SIN la guarda de Producción de la
/// siembra local: es lo que la hace posible en un entorno real y, a la vez, la línea más
/// peligrosa del diseño (el incidente de 2026-08-27 fue una siembra de demo en producción).
/// Sigue siendo correcta mientras solo se alcance desde el modo de CLI
/// <c>--sembrar-demo-direccion</c> de <c>Program.cs</c>, que termina el proceso antes del
/// arranque normal. Este trinquete vigila eso, con los controles positivos que evitan que
/// se quede ciego si se renombra algo: quién la llama, dónde está la llamada y en qué orden.
/// </summary>
public class SiembraDemoDireccionSoloDesdeElModoCliTests
{
    private const string Siembra = "SiembraDemoDireccionAdministrativa";
    private const string Modo = "if (args.Contains(SiembraDemoDireccionAdministrativa.ArgumentoSembrar))";

    [Fact]
    public void Solo_Program_invoca_la_siembra_administrativa()
    {
        var invocadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains(Siembra + ".SembrarAsync(", StringComparison.Ordinal) ||
                        Texto(f).Contains(Siembra + ".EjecutarAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RaizDelRepositorio(), f).Replace('\\', '/'))
            .ToList();

        invocadores.Should().BeEquivalentTo(["src/CaeManager.Web/Program.cs"],
            "un hosted service, un seeder o un endpoint que la llamara sembraría en producción sin el modo de CLI que la protege");
    }

    [Fact]
    public void SembrarRamaAsync_solo_lo_llaman_las_dos_siembras_conocidas()
    {
        var llamadores = FicherosDeCodigo()
            .Where(f => Texto(f).Contains("SembrarRamaAsync(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        llamadores.Should().BeEquivalentTo(
            ["EscenariosDireccionDemoSeeder.cs", "SiembraDemoDireccionAdministrativa.cs"],
            "es el camino sin la guarda de Producción: un tercer llamador es un tercer camino a producción sin revisar");
    }

    [Fact]
    public void En_Program_la_llamada_esta_dentro_del_modo_de_CLI_que_termina_el_proceso_y_antes_de_toda_siembra_de_arranque()
    {
        var program = File.ReadAllText(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        var modo = program.IndexOf(Modo, StringComparison.Ordinal);
        var primeraSiembraDeArranque = program.IndexOf("IdentitySeeder.SeedAsync(", StringComparison.Ordinal);

        modo.Should().BeGreaterThan(-1, "control positivo: el modo de CLI existe con esa forma");
        primeraSiembraDeArranque.Should().BeGreaterThan(-1, "control positivo: el instrumento ve la siembra de arranque");

        var apertura = program.IndexOf('{', modo);
        var profundidad = 0;
        var cierre = -1;
        for (var i = apertura; i < program.Length; i++)
        {
            if (program[i] == '{') profundidad++;
            else if (program[i] == '}' && --profundidad == 0) { cierre = i; break; }
        }

        cierre.Should().BeGreaterThan(apertura, "el bloque del modo de CLI está delimitado por llaves balanceadas");
        var bloque = program[(apertura + 1)..cierre];

        bloque.Should().Contain(Siembra + ".SembrarAsync(", "la llamada va DENTRO del modo de CLI");
        bloque.TrimEnd().Should().EndWith("return;",
            "el modo de CLI termina el proceso con return; sin levantar la aplicación: si faltara, la siembra seguiría al arranque normal");
        program.IndexOf(Siembra + ".SembrarAsync(", StringComparison.Ordinal).Should().BeInRange(apertura, cierre,
            "y no hay otra llamada fuera del bloque");
        cierre.Should().BeLessThan(primeraSiembraDeArranque, "el modo de CLI cierra antes de cualquier siembra del arranque normal");
    }

    [Fact]
    public void Los_argumentos_de_los_modos_de_CLI_son_los_que_documenta_el_runbook()
    {
        var clase = File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Infrastructure", "Persistence", "Seed", "SiembraDemoDireccionAdministrativa.cs"));

        clase.Should().Contain("ArgumentoSembrar = \"--sembrar-demo-direccion\"");
        clase.Should().Contain("ArgumentoRetirar = \"--retirar-demo-direccion\"");
    }

    private static string Texto(string fichero) => File.ReadAllText(fichero);

    private static IEnumerable<string> FicherosDeCodigo() =>
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                        !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

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
