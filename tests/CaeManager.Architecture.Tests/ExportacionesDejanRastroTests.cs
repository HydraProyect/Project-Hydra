using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// SEG-10 se abrió porque una ruta de exportación nueva no estaba obligada a dejar rastro. Por cada
/// fichero de endpoints: tantas llamadas a <c>RegistrarAsync</c> como rutas <c>.xlsx</c> o <c>.pdf</c>
/// generadas con datos del Tenant. Las plantillas en blanco no llevan datos y quedan fuera.
/// </summary>
public class ExportacionesDejanRastroTests
{
    private static readonly Regex RutaExportada = new(
        @"Map(?:Get|Post)\(\s*(?:""[^""]*\.(?:xlsx|pdf|csv)""|(?<constante>Ruta\w+))", RegexOptions.Compiled);

    private static readonly Regex ConstanteDeRuta = new(
        @"const string (?<nombre>Ruta\w+) = ""[^""]*\.(?:xlsx|pdf|csv)""", RegexOptions.Compiled);

    private static readonly Regex Plantilla = new(@"plantilla", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LlamadaAlRastro = new(@"registroExportacion\.RegistrarAsync\(", RegexOptions.Compiled);

    [Fact]
    public void Cada_ruta_de_exportacion_deja_su_rastro()
    {
        var web = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web");

        var rutasPorFichero = Directory.EnumerateFiles(web, "*Endpoints.cs", SearchOption.AllDirectories)
            .Select(f => (Fichero: Path.GetRelativePath(web, f), Fuente: File.ReadAllText(f)))
            .Select(f => (f.Fichero, f.Fuente, Rutas: RutasExportadas(f.Fuente)))
            .Where(f => f.Rutas > 0)
            .ToList();

        rutasPorFichero.Select(f => f.Fichero.Replace('\\', '/')).Should()
            .Contain("Reportes/ReportesEndpoints.cs", "control positivo: el instrumento ve las rutas del grupo /reportes");
        rutasPorFichero.Sum(f => f.Rutas).Should().BeGreaterThan(15, "control positivo: el instrumento ve las exportaciones");

        rutasPorFichero
            .Where(f => LlamadaAlRastro.Matches(f.Fuente).Count < f.Rutas)
            .Select(f => f.Fichero)
            .Should().BeEmpty("sin rastro una exportación masiva no deja constancia de quién se llevó los datos");
    }

    private static int RutasExportadas(string fuente)
    {
        var constantes = ConstanteDeRuta.Matches(fuente).Select(m => m.Groups["nombre"].Value).ToHashSet();
        return RutaExportada.Matches(fuente).Count(m => !Plantilla.IsMatch(m.Value)
            && (!m.Groups["constante"].Success || constantes.Contains(m.Groups["constante"].Value)));
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")) &&
               !Directory.Exists(Path.Combine(directorio.FullName, ".github")))
            directorio = directorio.Parent;

        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio.");
    }
}
