using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo comando con apt de los workflows pasa por la acción compuesta
/// <c>.github/actions/apt-acotado</c>.
///
/// <para>
/// <b>Por qué.</b> Desde las 22:50 UTC del 2026-10-31, algunos runners no
/// alcanzaban el espejo de Azure y <c>apt-get update</c> se quedaba parado sin
/// escribir nada hasta que el techo del job lo cortaba, a los 60 o 75 minutos.
/// Un solo job colgado deja el
/// <c>merge_group</c> sin responder y la cola expulsa la PR con
/// <c>checks_timed_out</c>. <c>playwright install --with-deps</c> lanza el mismo
/// <c>apt-get</c> por dentro, así que también entra en la regla.
/// </para>
/// </summary>
public class AptAcotadoEnCiTests
{
    private const string UsoDeLaAccion = "uses: ./.github/actions/apt-acotado";

    private static readonly Regex UsaApt = new(@"\bapt-get\b|--with-deps|\binstall-deps\b", RegexOptions.Compiled);

    private static readonly Regex InicioDePaso = new(@"^\s*- (name|uses|run|id|if|with|env):", RegexOptions.Compiled);

    [Fact]
    public void Ningun_comando_con_apt_de_los_workflows_queda_fuera_de_la_accion_acotada()
    {
        var workflows = Directory.EnumerateFiles(
            Path.Combine(RaizDelRepositorio(), ".github", "workflows"), "*.yml").ToList();
        workflows.Should().NotBeEmpty("el test tiene que estar mirando los workflows de verdad");

        var sinAcotar = new List<string>();
        var acotados = 0;

        foreach (var ruta in workflows)
        {
            var lineas = File.ReadAllLines(ruta);

            for (var i = 0; i < lineas.Length; i++)
            {
                if (lineas[i].TrimStart().StartsWith('#') || !UsaApt.IsMatch(lineas[i]))
                    continue;

                if (PasoDe(lineas, i).Any(linea => linea.Trim() == UsoDeLaAccion))
                    acotados++;
                else
                    sinAcotar.Add($"{Path.GetFileName(ruta)}:{i + 1}: {lineas[i].Trim()}");
            }
        }

        acotados.Should().BePositive("si no encuentra ningún comando con apt, el test no prueba nada");
        sinAcotar.Should().BeEmpty(
            "un apt sin límite puede colgar el job una hora y dejar la cola de fusión sin respuesta");
    }

    private static IEnumerable<string> PasoDe(string[] lineas, int indice)
    {
        var inicio = indice;
        while (inicio > 0 && !InicioDePaso.IsMatch(lineas[inicio]))
            inicio--;

        var fin = indice + 1;
        while (fin < lineas.Length && !InicioDePaso.IsMatch(lineas[fin]))
            fin++;

        return lineas[inicio..fin];
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
