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
/// </summary>
public class PromesaDeRecuperarDesdeAuditoriaTests
{
    private static readonly Regex Promesa = new(@"recuper\w*\s+desde\s+Auditor", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public void Solo_prometen_recuperar_desde_Auditoria_los_modulos_cuyo_tipo_se_puede_restaurar()
    {
        var restaurables = TiposRestaurables();
        var promesas = PromesasEnRecursos();

        // Controles positivos, independientes de qué textos prometan hoy algo: la
        // reflexión encuentra los comandos de restaurar, la regex reconoce la frase
        // retirada (con y sin tilde en el pronombre) y el recorrido lee recursos de
        // verdad. Si alguno falla, el gate sería ciego y daría verde vacío.
        restaurables.Should().Contain(["Trabajador", "Empresa", "Centro"]);
        Promesa.IsMatch("Se ocultará de las listas activas. Podrás recuperarlo desde Auditoría.").Should().BeTrue();
        Promesa.IsMatch("Podrás recuperarlas desde Auditoria").Should().BeTrue();
        Promesa.IsMatch("No se puede recuperar desde la aplicación.").Should().BeFalse();
        RecursosLeidos().Should().Contain("Vehiculos", "el recorrido tiene que llegar a los recursos de Vehículos");

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
