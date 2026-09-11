using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>PestanaActiva</c> es un parámetro <c>string</c> de <c>Pestanas</c> (y de
/// cada Context Workspace panel que lo reenvía). Razor solo trata el valor de
/// un atributo como C# si lleva <c>@</c> — sin él, <c>PestanaActiva="PestanaActiva"</c>
/// pasa el LITERAL «PestanaActiva», no el valor de la propiedad, y ninguna
/// pestaña queda marcada como activa (<c>aria-selected</c> nunca es «true»),
/// ni siquiera al abrir el panel por deep link.
///
/// <para>
/// Se vio en Vehículo, Trabajador, Cliente, Empresa y Centro (Subcontrata ya
/// lo tenía corregido). El patrón es siempre el mismo error de tecleo —
/// escribir el nombre del parámetro dos veces en vez de anteponer <c>@</c> a
/// la propiedad—, así que el ratchet vigila la forma del error, no una lista
/// de ficheros.
/// </para>
/// </summary>
public class PestanaActivaLlevaArrobaTests
{
    private static readonly Regex PatronSinArroba = new(
        @"\bPestanaActiva=""(?!@)", RegexOptions.Compiled);

    [Fact]
    public void Ningun_razor_pasa_PestanaActiva_como_literal_por_olvidar_la_arroba()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", "CaeManager.Web");

        var infractores = Directory
            .EnumerateFiles(directorio, "*.razor", SearchOption.AllDirectories)
            .Select(archivo => (Ruta: Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/'), archivo))
            .Where(x => PatronSinArroba.IsMatch(File.ReadAllText(x.archivo)))
            .Select(x => x.Ruta)
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, infractores).Should().BeEmpty(
            "PestanaActiva es un parámetro string: sin @ Razor le pasa el literal \"PestanaActiva\" en vez del " +
            "valor de la propiedad, y ninguna pestaña queda marcada como activa (aria-selected nunca es \"true\")");
    }

    /// <summary>Control positivo: sin esto, un patrón roto o demasiado estrecho pasaría igual que uno que no encuentra nada porque no hay nada que encontrar.</summary>
    [Fact]
    public void El_patron_detecta_el_caso_que_vigila_y_no_marca_el_caso_correcto()
    {
        const string sinArroba = "<Pestanas Definiciones=\"_pestanas\" PestanaActiva=\"PestanaActiva\" PestanaActivaChanged=\"PestanaActivaChanged\" />";
        const string conArroba = "<Pestanas Definiciones=\"_pestanas\" PestanaActiva=\"@PestanaActiva\" PestanaActivaChanged=\"PestanaActivaChanged\" />";
        const string variableDistinta = "<Pestanas Definiciones=\"_pestanas\" PestanaActiva=\"@_pestanaActiva\" PestanaActivaChanged=\"v => _pestanaActiva = v\" />";

        PatronSinArroba.IsMatch(sinArroba).Should().BeTrue(
            "control positivo: el patrón tiene que ver el error real que se vio en producción");
        PatronSinArroba.IsMatch(conArroba).Should().BeFalse(
            "con @ el parámetro está bien enlazado: no es un infractor");
        PatronSinArroba.IsMatch(variableDistinta).Should().BeFalse(
            "PestanaActivaChanged=\"PestanaActivaChanged\" reenvía el EventCallback (tipo no-string, Razor ya lo " +
            "trata como C# sin @) y no debe confundirse con el parámetro PestanaActiva vigilado aquí");
    }

    /// <summary>Guarda: si el recorrido de ficheros dejara de ver el árbol real, el test de arriba pasaría en vacío por ceguera, no por estar limpio.</summary>
    [Fact]
    public void Hay_razor_que_inspeccionar()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "src", "CaeManager.Web");

        Directory.EnumerateFiles(directorio, "*.razor", SearchOption.AllDirectories).Count()
            .Should().BeGreaterThan(100, "si esto es bajo, el propio recorrido de ficheros dejó de ver el árbol real");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
