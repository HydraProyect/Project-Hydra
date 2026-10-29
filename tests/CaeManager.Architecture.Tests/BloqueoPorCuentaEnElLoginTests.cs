using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// ASVS 4.0.3 V2.2.1: el inicio de sesión con contraseña cuenta los fallos en el bloqueo por cuenta.
/// Identity solo lo hace si quien llama a <c>PasswordSignInAsync</c> pasa <c>lockoutOnFailure: true</c>;
/// con <c>false</c> la configuración de bloqueo (5 intentos, 15 minutos, que fija
/// <c>ProveedorTotpDeUnSoloUsoTests</c> en Web.Tests) no surtiría efecto y la fuerza bruta no tendría
/// freno por cuenta.
///
/// <para>
/// <b>Lo que NO observa:</b> la llamada se reconoce por texto (sin comentarios), así que un
/// <c>lockoutOnFailure</c> pasado en una variable no se ve; el comportamiento real del bloqueo lo cubren los
/// tests de integración de <c>SignInManagerCuentaDesactivada</c>.
/// </para>
/// </summary>
public class BloqueoPorCuentaEnElLoginTests
{
    private const string Login = "src/CaeManager.Web/Components/Account/Pages/Login.razor";

    private static readonly Regex InicioConContrasena = new(
        @"\.PasswordSignInAsync\s*\((?<argumentos>[^;]*)\)\s*;", RegexOptions.Compiled);

    [Fact]
    public void El_login_con_contrasena_cuenta_los_fallos_en_el_bloqueo_de_la_cuenta()
    {
        var llamadas = InicioConContrasena.Matches(SinComentarios(Login));

        llamadas.Should().ContainSingle("control: Login.razor inicia sesión con contraseña en un solo sitio");
        llamadas[0].Groups["argumentos"].Value.Should().MatchRegex(@"lockoutOnFailure\s*:\s*true",
            "sin lockoutOnFailure: true los intentos fallidos no bloquean la cuenta (V2.2.1)");
    }

    [Fact]
    public void Ningun_inicio_de_sesion_desactiva_el_bloqueo_por_intentos_fallidos()
    {
        var raiz = RaizDelRepositorio();
        var infractores = Directory
            .EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(raiz, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(f => Regex.IsMatch(SinComentarios(f), @"lockoutOnFailure\s*:\s*false"))
            .ToList();

        infractores.Should().BeEmpty("ningún inicio de sesión puede saltarse el bloqueo por cuenta (V2.2.1)");
    }

    private static string SinComentarios(string relativo)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), relativo.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(ruta).Should().BeTrue($"control: {relativo} sigue donde este test lo busca");
        return LimpiadorDeComentarios.Quitar(File.ReadAllText(ruta), razor: relativo.EndsWith(".razor", StringComparison.Ordinal));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
