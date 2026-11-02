using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>CerrarTodasAsync</c> y <c>CerrarLasDemasAsync</c> van por <c>app_revocar_sesiones_de_cuenta</c>,
/// que exige contexto firmado y el Tenant de la cuenta pero no que la cuenta sea quien firma: restablecer
/// la contraseña y «No he sido yo» llegan por un enlace de correo, sin sesión de ella. Autorizar es cosa
/// de quien llama, y la política <c>solo_la_propia_cuenta_revoca</c> ya no hace de red en este camino:
/// un punto de llamada nuevo revocaría las sesiones de cualquier cuenta del Tenant sin nada que lo pare.
/// Por eso cada punto de llamada se añade aquí a propósito, tras comprobar qué prueba que quien llama es
/// la cuenta.
/// </summary>
public class PuntosDeCierreDeSesionesCongeladosTests
{
    private static readonly string[] PuntosPermitidos =
    [
        // El enlace de «No he sido yo» llegado al buzón de la cuenta.
        "src/CaeManager.Infrastructure/Seguridad/VigilanciaInicioSesionService.cs: CerrarTodasAsync",
        // La propia cuenta autenticada: la toma de su cookie.
        "src/CaeManager.Web/Components/Account/CierreSesionEnServidor.cs: CerrarTodasAsync",
        // El enlace de restablecer, con su token ya verificado por Identity.
        "src/CaeManager.Web/Components/Account/Pages/RestablecerContrasena.razor.cs: CerrarTodasAsync",
        // La propia cuenta autenticada, que acaba de cambiar su contraseña.
        "src/CaeManager.Web/Components/Account/Pages/CambiarContrasena.razor: CerrarLasDemasAsync",
    ];

    /// <summary>
    /// Toda mención en código, con o sin paréntesis, para que un grupo de métodos o un delegado también
    /// cuente. Solo quedan fuera las dos declaraciones de cada método, por su <c>Task&lt;int&gt;</c>.
    /// </summary>
    private static readonly Regex Mencion = new(
        @"(?<!Task<int> )\b(?<metodo>CerrarTodasAsync|CerrarLasDemasAsync)\b", RegexOptions.Compiled);

    [Fact]
    public void Los_puntos_de_llamada_que_revocan_varias_sesiones_son_los_permitidos()
    {
        var raiz = RaizDelRepositorio();
        var separador = Path.DirectorySeparatorChar;

        var encontrados = Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.*", SearchOption.AllDirectories)
            .Where(a => a.EndsWith(".cs", StringComparison.Ordinal) || a.EndsWith(".razor", StringComparison.Ordinal))
            .Where(a => !a.Contains($"{separador}bin{separador}", StringComparison.Ordinal)
                && !a.Contains($"{separador}obj{separador}", StringComparison.Ordinal))
            .SelectMany(fichero => File.ReadLines(fichero)
                .Where(linea => !EsComentario(linea))
                .SelectMany(linea => Mencion.Matches(linea))
                .Select(m => $"{Path.GetRelativePath(raiz, fichero).Replace('\\', '/')}: {m.Groups["metodo"].Value}"))
            .ToList();

        encontrados.Should().BeEquivalentTo(PuntosPermitidos,
            "un punto de llamada nuevo tiene que autorizar por sí mismo que actúa la cuenta: la RLS no lo hará");
    }

    private static bool EsComentario(string linea)
    {
        var codigo = linea.TrimStart();
        return codigo.StartsWith("//", StringComparison.Ordinal) || codigo.StartsWith('*') || codigo.StartsWith("@*", StringComparison.Ordinal);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
            ?? throw new DirectoryNotFoundException("No se encontró CaeManager.slnx subiendo desde el directorio de la prueba.");
    }
}
