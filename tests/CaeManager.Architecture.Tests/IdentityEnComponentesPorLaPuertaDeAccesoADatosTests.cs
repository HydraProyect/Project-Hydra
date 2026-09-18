using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Un componente que usa los gestores de Identity lo hace por
/// <c>PuertaAccesoDatos</c>.</b>
///
/// <para>
/// En Blazor Server el <c>CaeManagerDbContext</c> es scoped y lo comparte todo
/// el circuito; dos operaciones en vuelo sobre él lanzan «A second operation was
/// started on this context». Lo que entra por MediatR pasa por la puerta solo
/// (<c>SerializacionAccesoDatosBehavior</c>); <c>UserManager</c>,
/// <c>SignInManager</c> y <c>RoleManager</c> no, y hay que envolverlos a mano.
/// <c>PestanaHistorial</c> no lo hacía: buscaba usuarios fuera de la puerta y su
/// catch convertía el choque en «No pudimos cargar el historial».
/// </para>
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> los componentes (<c>.razor</c> más su
/// <c>.razor.cs</c>) que inyectan un gestor de Identity y en los que no aparece
/// ninguna llamada a <c>PuertaAccesoDatos.EjecutarAsync</c>. Esa lista se
/// congela por igualdad: un componente nuevo que use Identity sin la puerta la
/// hace crecer y pone esto en rojo; uno que se arregle la hace menguar y también,
/// para que la lista se recorte en el mismo commit.
/// </para>
///
/// <para>
/// <b>Lo que NO observa</b> (huecos declarados):
/// </para>
/// <list type="bullet">
/// <item>La granularidad es el componente, no la llamada: un componente que ya
/// usa la puerta en un sitio y añade otra llamada a <c>UserManager</c> fuera de
/// ella pasa en verde.</item>
/// <item>Solo los gestores de Identity inyectados en componentes. Un gestor
/// pasado como argumento a otro servicio, o servicios propios que toquen el
/// DbContext sin pasar por MediatR, quedan fuera.</item>
/// </list>
/// </summary>
public class IdentityEnComponentesPorLaPuertaDeAccesoADatosTests
{
    /// <summary>
    /// Componentes que hoy usan un gestor de Identity sin la puerta, con su motivo.
    /// </summary>
    private static readonly string[] SinPuertaCongelados =
    [
        // SSR estático (sin @rendermode): viven en el scope de una petición HTTP,
        // no en un circuito, y no comparten DbContext con otros componentes
        // interactivos cargando en paralelo.
        "src/CaeManager.Web/Components/Account/Pages/CambiarContrasena.razor",
        "src/CaeManager.Web/Components/Account/Pages/ConfigurarAutenticadorDosFactores.razor",
        "src/CaeManager.Web/Components/Account/Pages/Login.razor",
        "src/CaeManager.Web/Components/Account/Pages/LoginCon2fa.razor",
        "src/CaeManager.Web/Components/Account/Pages/OlvideContrasena.razor",
        "src/CaeManager.Web/Components/Account/Pages/RestablecerContrasena.razor",
        // Interactivo, pero el acceso solo corre al pulsar «Conectar» (pasa el
        // UserManager a EmisorTokenExtension.EmitirAsync): riesgo bajo, deuda
        // registrada, no aceptada como correcta.
        "src/CaeManager.Web/Features/Extension/Pages/ConectarExtension.razor",
    ];

    private static readonly Regex InyectaGestorDeIdentity = new(
        @"(?m)(?:^\s*@inject\s+|\[Inject\][^\n]*?)(?:\w+\.)*(?:UserManager|SignInManager|RoleManager)\s*<",
        RegexOptions.Compiled);

    private static readonly Regex PasaPorLaPuerta = new(
        @"\bPuertaAccesoDatos\s*\.\s*EjecutarAsync\s*[<(]", RegexOptions.Compiled);

    [Fact]
    public void Los_componentes_que_usan_Identity_sin_la_puerta_son_exactamente_los_congelados()
    {
        var conIdentity = ComponentesQueInyectanIdentity();

        conIdentity.Should().Contain("src/CaeManager.Web/Components/Layout/MainLayout.razor",
            "MainLayout inyecta UserManager y lo usa por la puerta: si el descubrimiento no lo ve, ha dejado " +
            "de observar el fenómeno y la comparación de abajo pasaría en vacío");

        var sinPuerta = conIdentity
            .Where(c => !FicherosDelComponente(c).Any(f => PasaPorLaPuerta.IsMatch(File.ReadAllText(Absoluta(f)))))
            .ToList();

        sinPuerta.Should().BeEquivalentTo(SinPuertaCongelados,
            "UserManager/SignInManager/RoleManager no pasan por MediatR: en un circuito de Blazor Server hay que " +
            "envolverlos en PuertaAccesoDatos.EjecutarAsync (ver MainLayout.razor.cs). Si arreglaste uno de los " +
            "congelados, quítalo de la lista en el mismo commit");
    }

    private static List<string> ComponentesQueInyectanIdentity()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");
        var separador = Path.DirectorySeparatorChar;

        return Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{separador}obj{separador}") && !a.Contains($"{separador}bin{separador}"))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(separador, '/'))
            .Where(c => FicherosDelComponente(c).Any(f => InyectaGestorDeIdentity.IsMatch(File.ReadAllText(Absoluta(f)))))
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>El <c>.razor</c> y, si existe, su <c>.razor.cs</c>: son el mismo componente.</summary>
    private static IEnumerable<string> FicherosDelComponente(string razorRelativo)
    {
        yield return razorRelativo;

        var codeBehind = razorRelativo + ".cs";
        if (File.Exists(Absoluta(codeBehind))) yield return codeBehind;
    }

    private static string Absoluta(string relativa) =>
        Path.Combine(RaizDelRepositorio(), relativa.Replace('/', Path.DirectorySeparatorChar));

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
