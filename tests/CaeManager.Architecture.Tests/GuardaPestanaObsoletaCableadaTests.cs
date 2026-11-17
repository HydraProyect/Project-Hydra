using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Guarda de pestaña obsoleta (D-6 b, R-2): su comportamiento lo prueban <c>GuardaSeleccionVigenteTests</c> y
/// <c>SesionSoporteEndpointsTests</c> (Web) y el orden en el pipeline, <c>GuardaPestanaObsoletaRegistroTests</c>
/// (Application). Este trinquete vigila el cableado que esos tests no ven, porque el proyecto no tiene un
/// <c>WebApplicationFactory</c>: que <c>Program.cs</c> registre la guarda de Web, que cada circuito adopte su
/// selección y que todo sitio que escriba o borre la cookie de contexto actualice la selección vigente. Sin lo
/// primero la guarda de Web desaparece en silencio (Application registra una que no rechaza nada); sin lo último, la
/// petición siguiente, con la cookie nueva, se rechazaría a sí misma.
/// </summary>
public partial class GuardaPestanaObsoletaCableadaTests
{
    /// <summary>
    /// Sitios que borran la cookie de contexto sin actualizar la selección vigente, con su motivo. El cierre de sesión
    /// termina la sesión de navegador: la clave del registro lleva su <c>talveg:sid</c> y la siguiente es otra.
    /// </summary>
    private static readonly Dictionary<string, string> SinRegistroDeVigente = new()
    {
        ["src/CaeManager.Web/Components/Account/IdentityEndpointsExtensions.cs"] =
            "cierre de sesión: la clave del registro (usuario y talveg:sid) muere con la sesión de navegador",
    };

    [GeneratedRegex(@"Cookies\.(Append|Delete)\(\s*ClienteActivoSeleccionado\.NombreCookie")]
    private static partial Regex EscribeLaCookieDeContexto();

    [Fact]
    public void Program_registra_la_guarda_de_Web_y_su_registro_de_selecciones()
    {
        var program = Leer("src/CaeManager.Web/Program.cs");

        program.Should().Contain("AddSingleton<CaeManager.Web.Services.RegistroSeleccionVigente>()",
            "un registro por ámbito no vería los cambios de las otras pestañas");
        program.Should().Contain("AddScoped<IGuardaSeleccionVigente, CaeManager.Web.Services.GuardaSeleccionVigente>()",
            "sin ella queda la de Application, que no rechaza nada");
    }

    [Fact]
    public void Cada_circuito_adopta_la_seleccion_de_su_negociacion()
    {
        Leer("src/CaeManager.Web/Services/RevalidacionCircuitoActivoHandler.cs")
            .Should().Contain("AdoptarComoVigente(registroSeleccionVigente)",
                "sin la adopción, una respuesta de cambio perdida deja la pestaña pidiendo recargar sin fin");
    }

    [Fact]
    public void Todo_sitio_que_escribe_o_borra_la_cookie_de_contexto_actualiza_la_seleccion_vigente()
    {
        var raiz = RaizDelRepositorio();
        var sitios = Directory.EnumerateFiles(Path.Combine(raiz, "src", "CaeManager.Web"), "*.cs", SearchOption.AllDirectories)
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Texto: SinComentarios(File.ReadAllText(f))))
            .Where(f => EscribeLaCookieDeContexto().IsMatch(f.Texto))
            .ToList();

        sitios.Should().NotBeEmpty("el instrumento tiene que ver al menos SesionSoporteEndpoints");
        foreach (var (ruta, texto) in sitios.Where(s => !SinRegistroDeVigente.ContainsKey(s.Ruta)))
            texto.Should().Contain("CookieDeContextoTenant.RegistrarSeleccionVigente(",
                $"{ruta} cambia la cookie de contexto: la selección que deja tiene que pasar a ser la vigente");
    }

    private static string Leer(string ruta) => SinComentarios(File.ReadAllText(Path.Combine(RaizDelRepositorio(), ruta)));

    /// <summary>
    /// Sin comentarios de línea ni de documentación: una llamada comentada no cablea nada, y el trinquete tiene que
    /// verla como ausente (comprobado comentando la adopción del handler y el registro de <c>Program.cs</c>).
    /// </summary>
    private static string SinComentarios(string texto) => LineaComentada().Replace(texto, string.Empty);

    [GeneratedRegex(@"//.*")]
    private static partial Regex LineaComentada();

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
