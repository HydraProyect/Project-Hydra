using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// El puerto de decisiones cerradas del asistente
/// (<c>IDecisionesCerradasAsistenteService</c>) no nombra al proveedor que lo
/// implementa hoy. El nombre del proveedor, su modelo y su endpoint viven solo en
/// Infrastructure.
///
/// Es una propiedad que se pierde sin ruido: basta un comentario o una constante
/// «de conveniencia» en Application para que el siguiente cambio de proveedor
/// arrastre la capa que no debería enterarse. Mismo mecanismo de ratchet por
/// texto que <see cref="ClientesDeIaConResilienciaPropiaTests"/>.
/// </summary>
public class ProveedorDeDecisionesCerradasFueraDeApplicationTests
{
    /// <summary>Anthropic, el proveedor vigente de las decisiones cerradas.</summary>
    private static readonly Regex ProveedorVigente = new(@"(?i)\banthropic\b", RegexOptions.Compiled);

    /// <summary>
    /// Proveedores de IA retirados (decisión de proveedor único, 2026-10-29) y los
    /// nombres de modelo y endpoint del adaptador anterior de decisiones cerradas.
    /// </summary>
    private static readonly Regex ProveedoresRetirados = new(
        @"(?i)\b(jev|typesafe|type\s*safe|system\s*one|systemone|gemini|mistral)\b|jev-\d|api\.typesafe",
        RegexOptions.Compiled);

    /// <summary>
    /// Los dos sitios de Application que nombran a Anthropic a propósito: la
    /// configuración y la consulta del chat normativo, que no pasan por el puerto
    /// de decisiones cerradas.
    /// </summary>
    private static readonly string[] ConsultaNormativa =
    [
        "src/CaeManager.Application/AsistenteIa/AsistenteConsultaOptions.cs",
        "src/CaeManager.Application/AsistenteIa/Queries/PreguntarAlAsistente/",
    ];

    [Fact]
    public void Application_no_nombra_al_proveedor_de_decisiones_cerradas()
    {
        var infractores = Directory
            .EnumerateFiles(Ruta("src/CaeManager.Application/AsistenteIa"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !EsGenerado(f))
            .Where(f => !ConsultaNormativa.Any(e => Relativa(f).StartsWith(e, StringComparison.Ordinal)))
            .Where(f => ProveedorVigente.IsMatch(File.ReadAllText(f)) || ProveedoresRetirados.IsMatch(File.ReadAllText(f)))
            .ToList();

        infractores.Should().BeEmpty(
            "el puerto expresa decisiones cerradas, no un proveedor: su nombre, su modelo y su endpoint van en Infrastructure");
    }

    [Fact]
    public void Application_no_nombra_a_ningun_proveedor_de_IA_retirado()
    {
        var infractores = Directory
            .EnumerateFiles(Ruta("src/CaeManager.Application"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !EsGenerado(f))
            .Where(f => ProveedoresRetirados.IsMatch(File.ReadAllText(f)))
            .ToList();

        infractores.Should().BeEmpty("la IA tiene un único proveedor, Anthropic, sin caída a otro");
    }

    /// <summary>
    /// Fuera de Application, la única mención permitida de un proveedor retirado
    /// son las etiquetas de la pantalla de auditoría de IA, que siguen nombrando
    /// los registros históricos de <c>AuditoriasExtraccionIa</c>.
    /// </summary>
    [Fact]
    public void Ningun_proveedor_de_IA_retirado_vuelve_al_codigo()
    {
        const string etiquetasHistoricas = "src/CaeManager.Web/Features/AuditoriaIa/Pages/AuditoriaIa.razor.cs";

        var infractores = Directory
            .EnumerateFiles(Ruta("src"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal)
                || Path.GetFileName(f).StartsWith("appsettings", StringComparison.Ordinal))
            .Where(f => !EsGenerado(f))
            .Where(f => !EsMigracion(f))
            .Where(f => Relativa(f) != etiquetasHistoricas)
            .Where(f => ProveedoresRetirados.IsMatch(File.ReadAllText(f)))
            .Select(Relativa)
            .ToList();

        infractores.Should().BeEmpty("se retiraron Gemini, Mistral OCR y TypeSafe; ningún adaptador, opción ni siembra los usa");
    }

    /// <summary>
    /// Guarda del instrumento: si el patrón dejara de reconocer al proveedor, el
    /// test principal daría verde sin mirar nada. El adaptador lo nombra, así que
    /// ahí tiene que casar.
    /// </summary>
    [Fact]
    public void El_detector_reconoce_al_proveedor_donde_si_debe_estar()
    {
        var adaptador = Ruta("src/CaeManager.Infrastructure/AsistenteIa/AnthropicDecisionesCerradasService.cs");

        File.Exists(adaptador).Should().BeTrue("si el adaptador cambia de sitio, hay que reapuntar esta guarda");
        ProveedorVigente.IsMatch(File.ReadAllText(adaptador)).Should().BeTrue();
        ProveedoresRetirados.IsMatch(File.ReadAllText(
            Ruta("src/CaeManager.Web/Features/AuditoriaIa/Pages/AuditoriaIa.razor.cs"))).Should().BeTrue(
            "las etiquetas históricas nombran a los proveedores retirados: si no casan, el patrón está roto");

        Directory.EnumerateFiles(Ruta("src/CaeManager.Application/AsistenteIa/Decisiones"), "*.cs")
            .Should().NotBeEmpty("el puerto vigilado tiene que seguir existiendo");
        foreach (var excepcion in ConsultaNormativa)
            (File.Exists(Ruta(excepcion)) || Directory.Exists(Ruta(excepcion))).Should().BeTrue(
                $"la excepción {excepcion} tiene que seguir existiendo; si se movió, hay que reapuntarla");
    }

    private static bool EsGenerado(string archivo) =>
        archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool EsMigracion(string archivo) =>
        archivo.Contains($"{Path.DirectorySeparatorChar}Migrations", StringComparison.Ordinal);

    private static string Relativa(string archivo) =>
        Path.GetRelativePath(RaizDelRepositorio(), archivo).Replace(Path.DirectorySeparatorChar, '/');

    private static string Ruta(string relativa) =>
        Path.Combine(RaizDelRepositorio(), relativa.Replace('/', Path.DirectorySeparatorChar));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
