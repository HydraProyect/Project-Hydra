using System.Text.RegularExpressions;
using System.Xml.Linq;
using CaeManager.Application.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// FS-08 (auditoría UX de flujos sin salida, 2026-09-24): los diálogos de eliminar
/// Vehículo, Proyecto y Gestión prometían «Podrás recuperarlo desde Auditoría», y
/// Auditoría solo restaura los tipos que tienen un <c>Restaurar{Tipo}Command</c>.
/// La promesa llevaba al usuario a una pantalla sin salida.
///
/// <para>
/// El gate deriva los tipos restaurables de los propios comandos de Application
/// (no de una lista a mano): un recurso <c>Textos{Modulo}.resx</c> solo puede
/// prometer recuperar algo «desde Auditoría» si <c>{Modulo}</c> empieza por el
/// nombre de un tipo con <c>Restaurar{Tipo}Command</c> (TextosTrabajadores →
/// Trabajador). Si mañana existe <c>RestaurarVehiculoCommand</c>, la promesa vuelve
/// a ser verdad y el gate la acepta sin tocarlo.
/// </para>
/// <para>
/// Lee los recursos <c>.resx</c> y el marcado <c>.razor</c> de <c>Features/{Modulo}</c>
/// (sin comentarios Razor), y reconoce «recuperar» o «restaurar» seguidos, a pocas
/// palabras, de «desde/en (la pantalla de) Auditoría». Hueco declarado: compara el
/// módulo del fichero, no la entidad de cada frase, y solo reconoce la redacción en
/// castellano (el catalán es hoy copia literal del castellano).
/// </para>
/// </summary>
public class PromesaDeRecuperarDesdeAuditoriaTests
{
    private static readonly Regex Promesa = new(
        @"(recuper|restaur)\w*(\s+\w+){0,3}\s+(desde|en)\s+(la\s+pantalla\s+de\s+)?Auditor",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Solo_prometen_recuperar_desde_Auditoria_los_modulos_cuyo_tipo_se_puede_restaurar()
    {
        var restaurables = TiposRestaurables();
        var promesas = PromesasEnRecursos().Concat(PromesasEnMarcado()).ToList();

        // Controles positivos, independientes de qué textos prometan hoy algo: la
        // reflexión encuentra los comandos de restaurar, la regex reconoce la frase
        // retirada (con y sin tilde en el pronombre) y el recorrido lee recursos de
        // verdad. Si alguno falla, el gate sería ciego y daría verde vacío.
        restaurables.Should().Contain(["Trabajador", "Empresa", "Centro"]);
        Promesa.IsMatch("Se ocultará de las listas activas. Podrás recuperarlo desde Auditoría.").Should().BeTrue();
        Promesa.IsMatch("Podrás recuperarlas desde Auditoria").Should().BeTrue();
        Promesa.IsMatch("Podrás restaurarlo en Auditoría.").Should().BeTrue();
        Promesa.IsMatch("Un Administrador puede recuperar el proyecto desde la pantalla de Auditoría").Should().BeTrue();
        Promesa.IsMatch("No se puede recuperar desde la aplicación.").Should().BeFalse();
        RecursosLeidos().Should().Contain("Vehiculos", "el recorrido tiene que llegar a los recursos de Vehículos");
        MarcadoLeido().Should().Contain("Proyectos", "el recorrido tiene que llegar a las páginas de Proyectos");
        SinComentariosRazor("a @* Podrás recuperarlo desde Auditoría *@ b").Should().Be("a  b");

        var falsas = promesas
            .Where(p => !restaurables.Any(t => p.Modulo.StartsWith(t, StringComparison.Ordinal)))
            .Select(p => $"{p.Fichero} [{p.Clave}]: «{p.Valor}»")
            .ToList();

        falsas.Should().BeEmpty(
            "prometer «recuperar desde Auditoría» sin un Restaurar{Tipo}Command deja al usuario " +
            "en una pantalla que no puede deshacer nada (FS-08). Retira la promesa o implementa la restauración");
    }

    private static HashSet<string> TiposRestaurables() =>
        typeof(ICommand).Assembly.GetTypes()
            .Select(t => Regex.Match(t.Name, "^Restaurar(?<tipo>[A-Z][A-Za-z]+)Command$"))
            .Where(m => m.Success)
            .Select(m => m.Groups["tipo"].Value)
            .ToHashSet();

    private sealed record PromesaEncontrada(string Fichero, string Modulo, string Clave, string Valor);

    private static List<PromesaEncontrada> PromesasEnRecursos()
    {
        var raiz = RaizDelRepositorio();

        return Directory.EnumerateFiles(Path.Combine(raiz, "src"), "Textos*.resx", SearchOption.AllDirectories)
            .SelectMany(fichero =>
            {
                var nombre = Path.GetFileName(fichero);
                var modulo = nombre["Textos".Length..nombre.IndexOf('.')];
                return XDocument.Load(fichero).Root!.Elements("data")
                    .Select(d => (Clave: (string)d.Attribute("name")!, Valor: (string?)d.Element("value") ?? string.Empty))
                    .Where(d => Promesa.IsMatch(d.Valor))
                    .Select(d => new PromesaEncontrada(Path.GetRelativePath(raiz, fichero), modulo, d.Clave, d.Valor));
            })
            .ToList();
    }

    private static List<PromesaEncontrada> PromesasEnMarcado()
    {
        var raiz = RaizDelRepositorio();

        return FicherosDeMarcado(raiz)
            .SelectMany(f => SinComentariosRazor(File.ReadAllText(f.Fichero)).Split('\n')
                .Select((linea, i) => (Linea: linea.Trim(), Numero: i + 1))
                .Where(l => Promesa.IsMatch(l.Linea))
                .Select(l => new PromesaEncontrada(Path.GetRelativePath(raiz, f.Fichero), f.Modulo, $"línea {l.Numero}", l.Linea)))
            .ToList();
    }

    private static IEnumerable<(string Fichero, string Modulo)> FicherosDeMarcado(string raiz)
    {
        var features = Path.Combine(raiz, "src", "CaeManager.Web", "Features");
        return Directory.EnumerateFiles(features, "*.razor", SearchOption.AllDirectories)
            .Select(f => (f, Path.GetRelativePath(features, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0]));
    }

    private static HashSet<string> MarcadoLeido() =>
        FicherosDeMarcado(RaizDelRepositorio()).Select(f => f.Modulo).ToHashSet();

    private static string SinComentariosRazor(string texto) =>
        Regex.Replace(texto, @"@\*.*?\*@", string.Empty, RegexOptions.Singleline);

    private static HashSet<string> RecursosLeidos() =>
        Directory.EnumerateFiles(Path.Combine(RaizDelRepositorio(), "src"), "Textos*.resx", SearchOption.AllDirectories)
            .Select(f => Path.GetFileName(f))
            .Select(n => n["Textos".Length..n.IndexOf('.')])
            .ToHashSet();

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
