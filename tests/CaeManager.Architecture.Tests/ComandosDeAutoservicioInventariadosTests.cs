using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>IComandoDeAutoservicio</c> saca un comando de la lista de roles de escritura
/// de <c>AutorizacionEscrituraBehavior</c>: lo ejecuta también Consulta (y Cliente).
/// Es una puerta de autorización, así que el inventario se congela aquí. Un comando
/// nuevo que la implemente tiene que verse en la revisión de ESTE test y cumplir las
/// tres condiciones del marcador (usuario de <c>ICurrentUserService</c>, dato que
/// solo es suyo, misma respuesta para «no existe» y «es de otro»).
///
/// <para>
/// <b>Fuera a propósito</b>, tras revisar su handler: <c>RegistrarTramoGestionCommand</c>
/// (lo lee <c>ObtenerKpisBpoQuery</c> como ocupación de cada Gestor CAE),
/// <c>RegistrarHistorialInformeCommand</c> (historial del Tenant, con Cliente del
/// request), <c>GuardarOrdenMenuLateralCommand</c> (orden global del menú) y
/// <c>GuardarFirmaGuardadaUsuarioCommand</c> (la firma es propia, pero solo sirve
/// para firmar documentos, que Consulta no puede).
/// </para>
/// </summary>
public class ComandosDeAutoservicioInventariadosTests
{
    private static readonly HashSet<string> ComandosEsperados =
    [
        "AceptarTerminosCommand",
        "GuardarFiltroCommand",
        "EliminarFiltroGuardadoCommand",
        "RegistrarUsoRecienteCommand",
        "GuardarPreferenciaDashboardCommand",
        "MarcarNotificacionLeidaCommand",
    ];

    // Admite record o class, con o sin lista de parámetros (AceptarTerminosCommand no
    // la tiene): un patrón que exigiera paréntesis no vería ese caso y el trinquete
    // se quedaría ciego justo con el comando que motivó el marcador.
    private static readonly Regex Declaracion = new(
        @"public\s+(?:sealed\s+)?(?:record|class)\s+(\w+)\s*(?:\([^)]*\))?[^;{]*:\s*[^;{]*\bIComandoDeAutoservicio\b",
        RegexOptions.Compiled | RegexOptions.Singleline);

    [Fact]
    public void Exactamente_los_comandos_declarados_implementan_IComandoDeAutoservicio()
    {
        var (encontrados, ficherosQueLoNombran) = Medir(Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Application"));

        encontrados.Should().BeEquivalentTo(ComandosEsperados,
            "IComandoDeAutoservicio deja escribir a Consulta: un comando nuevo que lo implemente, o uno de la " +
            "lista que deje de hacerlo, tiene que verse aquí y no perderse en el resto del repositorio");

        // Contrapeso del patrón: si algún fichero nombra el marcador y el patrón no ve
        // ahí ninguna declaración, el patrón se ha quedado corto (una forma de declarar
        // que no reconoce) y el inventario de arriba ya no es de fiar.
        ficherosQueLoNombran.Should().Be(encontrados.Count,
            "cada fichero que nombra IComandoDeAutoservicio fuera del marcador y del behavior debe declarar un comando que el patrón ve");
    }

    [Fact]
    public void El_patron_ve_las_formas_de_declarar_un_comando()
    {
        var formas = new[]
        {
            "public record SinParametrosCommand : ICommand, IComandoDeAutoservicio;",
            "public record ConParametrosCommand(Guid Id, string Nombre) : ICommand<Guid>, IComandoDeAutoservicio;",
            "public sealed record SelladoCommand(Guid Id)\n    : ICommand,\n      IComandoDeAutoservicio;",
            "public class ClaseCommand : ICommand, IComandoDeAutoservicio { }",
        };

        formas.Select(f => Declaracion.Match(f).Groups[1].Value).Should().Equal(
            "SinParametrosCommand", "ConParametrosCommand", "SelladoCommand", "ClaseCommand");
        Declaracion.IsMatch("public record OtroCommand(Guid Id) : ICommand;").Should().BeFalse();
    }

    private static (HashSet<string> Encontrados, int FicherosQueLoNombran) Medir(string directorio)
    {
        var encontrados = new HashSet<string>();
        var ficheros = 0;

        foreach (var archivo in Directory.EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories))
        {
            var nombre = Path.GetFileName(archivo);
            if (nombre is "IComandoDeAutoservicio.cs" or "AutorizacionEscrituraBehavior.cs"
                || archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var contenido = File.ReadAllText(archivo);
            if (!Regex.IsMatch(contenido, @"\bIComandoDeAutoservicio\b"))
                continue;

            ficheros++;
            foreach (Match m in Declaracion.Matches(contenido))
                encontrados.Add(m.Groups[1].Value);
        }

        return (encontrados, ficheros);
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
