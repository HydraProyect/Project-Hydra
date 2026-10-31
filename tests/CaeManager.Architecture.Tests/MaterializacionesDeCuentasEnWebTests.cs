using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>La capa Web no carga cuentas completas para presentar datos</b> (ASVS 4.0.3
/// V4.1.3, mínimo privilegio). Una cuenta materializada trae también sus columnas
/// de credenciales: la lectura de presentación va por una proyección
/// (<c>ObtenerNombreCuentaQuery</c>, <c>CuentaVisibleEnRol</c>), de modo que quien
/// solo pinta un nombre no necesita leer nada más.
///
/// <para>
/// <b>Lo que SÍ observa:</b> en cada <c>.cs</c> o <c>.razor</c> de
/// <c>src/CaeManager.Web</c>, sin comentarios, las llamadas a los métodos de Identity
/// que devuelven una cuenta (<c>FindBy…Async</c>, <c>GetUserAsync</c>,
/// <c>GetUsersInRoleAsync</c>, <c>GetTwoFactorAuthenticationUserAsync</c>,
/// <c>ValidateSecurityStampAsync</c>),
/// <c>UserManager.Users</c> y <c>ObtenerVisiblesAsync</c> del directorio. El recuento
/// por fichero tiene que ser exactamente <see cref="Congeladas"/>: uno que crece pone
/// esto en rojo, y uno que mengua también, para que se rebaje en el mismo commit.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados):
/// </para>
/// <list type="bullet">
/// <item>Infrastructure: los consumidores de autenticación y revocación viven allí.</item>
/// <item>Si la lectura restante usa de verdad las columnas de credenciales: cuenta
/// llamadas, no columnas.</item>
/// <item>Otro camino hasta la cuenta sin estos nombres, como <c>IUserStore&lt;T&gt;</c>
/// directo. Para el nombre de los gestores vale
/// <see cref="UserManagerSoloDesdeInfraestructuraDeLoginTests"/>.</item>
/// <item>Se descartan comentarios, no literales de texto.</item>
/// </list>
/// </summary>
public class MaterializacionesDeCuentasEnWebTests
{
    /// <summary>
    /// Lo que queda en Web, con su motivo. Solo puede menguar: un sitio nuevo que solo
    /// presenta datos usa una proyección.
    /// </summary>
    private static readonly Dictionary<string, int> Congeladas = new()
    {
        // Sesión, autenticación y credenciales de la propia cuenta. Lo de CurrentUserService es una
        // lectura de UserManager.Users que ya proyecta (se cuentan llamadas, no columnas).
        ["src/CaeManager.Web/Components/Account/InicioSesionMicrosoft.cs"] = 3,
        ["src/CaeManager.Web/Components/Account/Pages/CambiarContrasena.razor"] = 1,
        ["src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor"] = 1,
        ["src/CaeManager.Web/Components/Account/Pages/Login.razor"] = 1,
        ["src/CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor"] = 1,
        ["src/CaeManager.Web/Components/Account/Pages/RestablecerContrasena.razor.cs"] = 1,
        ["src/CaeManager.Web/Services/CurrentUserService.cs"] = 1,
        ["src/CaeManager.Web/Services/RecuperacionContrasena.cs"] = 1,
        // Preferencias de la propia cuenta: la entidad se guarda después.
        ["src/CaeManager.Web/Components/Account/IdiomaEndpoints.cs"] = 2,
        ["src/CaeManager.Web/Components/Layout/SelectorTema.razor.cs"] = 2,
        ["src/CaeManager.Web/Services/ActividadUsuarioService.cs"] = 1,
        // La lista de /usuarios: calcula «pendiente de activación» con la contraseña.
        ["src/CaeManager.Web/Features/Usuarios/Pages/Usuarios.razor.cs"] = 1,
    };

    private static readonly Regex MaterializaUnaCuenta = new(
        @"(?<![Rr]oleManager)\.(?:FindBy(?:Id|Email|Name|Login)Async|GetUserAsync|GetUsersInRoleAsync" +
        @"|GetTwoFactorAuthenticationUserAsync|ObtenerVisiblesAsync|ValidateSecurityStampAsync)\s*\(|[Uu]ser[Mm]anager\.Users\b",
        RegexOptions.Compiled);

    [Fact]
    public void Web_solo_materializa_cuentas_en_los_sitios_congelados()
    {
        var encontradas = MaterializacionesPorFichero();

        // Control positivo: si el escaneo no ve a los consumidores que seguro existen,
        // ha dejado de observar y la igualdad de abajo no diría nada.
        encontradas.Should().ContainKeys(
            ["src/CaeManager.Web/Components/Account/Pages/Login.razor", "src/CaeManager.Web/Services/CurrentUserService.cs"],
            "el escaneo tiene que ver a los consumidores que seguro existen");

        encontradas.Should().BeEquivalentTo(Congeladas,
            "una lectura de presentación no materializa la cuenta: usa ObtenerNombreCuentaQuery o una proyección " +
            "del directorio. Si migraste un sitio, rebaja su recuento en el mismo commit");
    }

    /// <summary>
    /// <c>ILecturaCuentaPropia</c> acepta cualquier identificador y devuelve roles: fuera de estos sitios sería
    /// el rol de origen con otro nombre, y el rol de origen no autoriza
    /// (<see cref="RolDeOrigenFueraDeAutorizacionTests"/>). Solo la usan el guard de la propia sesión y
    /// <c>ObtenerRolOrigenAsync</c>.
    /// </summary>
    [Fact]
    public void La_lectura_de_la_cuenta_propia_solo_tiene_los_consumidores_congelados()
    {
        var raiz = RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;

        var consumidores = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Where(a => LimpiadorDeComentarios.Quitar(File.ReadAllText(a), razor: a.EndsWith(".razor", StringComparison.Ordinal))
                .Contains("ILecturaCuentaPropia", StringComparison.Ordinal))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'));

        consumidores.Should().BeEquivalentTo(
            "src/CaeManager.Infrastructure/DependencyInjection/InfrastructureServiceCollectionExtensions.cs",
            "src/CaeManager.Infrastructure/Identity/LecturaCuentaPropia.cs",
            "src/CaeManager.Web/Components/Layout/MainLayout.razor.cs",
            "src/CaeManager.Web/Services/CurrentUserService.cs");
    }

    [Theory]
    [InlineData("\"Hola\".Equals(rol); await RoleManager.FindByIdAsync(id);", 0)]
    [InlineData("await UserManager.FindByIdAsync(id);", 1)]
    [InlineData("await signInManager.ValidateSecurityStampAsync(principal);", 1)]
    [InlineData("await userManager.Users.AsNoTracking().CountAsync();", 1)]
    [InlineData("await _userManager.Users.CountAsync();", 1)]
    [InlineData("await DirectorioUsuarios.ObtenerVisiblesAsync(token);", 1)]
    [InlineData("await DirectorioUsuarios.ObtenerVisiblesEnRolAsync(rol, token);", 0)]
    public void El_detector_distingue_cuentas_de_roles_y_de_proyecciones(string codigo, int esperadas)
    {
        MaterializaUnaCuenta.Matches(codigo).Count.Should().Be(esperadas);
    }

    private static Dictionary<string, int> MaterializacionesPorFichero()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var separador = Path.DirectorySeparatorChar;

        return Directory.EnumerateFiles(web, "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => new
            {
                Relativa = Path.GetRelativePath(raiz, a).Replace(separador, '/'),
                Cuenta = MaterializaUnaCuenta.Matches(
                    LimpiadorDeComentarios.Quitar(File.ReadAllText(a), razor: a.EndsWith(".razor", StringComparison.Ordinal))).Count,
            })
            .Where(f => f.Cuenta > 0)
            .ToDictionary(f => f.Relativa, f => f.Cuenta);
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
