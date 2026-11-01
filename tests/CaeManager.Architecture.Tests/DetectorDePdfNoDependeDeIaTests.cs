using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Invariante de Dividir: en la detección ninguna página sale de TALVEG. En un PDF fusionado el tipo aún no se conoce,
/// así que la guarda de salud (<c>TipoDocumento.LecturaIaActiva</c>) no podría actuar: la única garantía posible es que
/// la detección no pueda llegar a ningún proveedor.
///
/// Mira dos cosas porque cada una tiene su punto ciego. Por tipos, las dependencias reales de los handlers de la
/// detección, en todo el árbol de constructores; por texto, todo el código de Herramientas PDF de Application e
/// Infrastructure, que es lo único que ve un <c>IServiceProvider</c> o un puerto citado sin inyectar. Y lo que un handler
/// pida por <c>IMediator</c> no aparece en ningún constructor, así que se exige también que solo pida el catálogo.
/// </summary>
public class DetectorDePdfNoDependeDeIaTests
{
    private static readonly string[] DirectoriosDeLaHerramienta =
    [
        "src/CaeManager.Application/HerramientasPdf",
        "src/CaeManager.Infrastructure/HerramientasPdf",
    ];

    /// <summary>
    /// El orquestador sí delega en el alta normal, que encola el análisis tras crear y lo guarda por tipo: esa es la
    /// frontera acordada, y está fuera de la detección.
    /// </summary>
    private const string FicheroDelOrquestador = "CrearDocumentosDesdeDivisionCommand.cs";

    private static readonly string[] ManejadoresDeLaDeteccion =
    [
        "CaeManager.Application.HerramientasPdf.PrepararDivisionPdfQueryHandler",
        "CaeManager.Application.HerramientasPdf.ObtenerCatalogoDivisionPdfQueryHandler",
    ];

    private static readonly string[] ImplementacionesDeLaDeteccion =
    [
        "CaeManager.Infrastructure.HerramientasPdf.PdfSharpDivisorPdf",
        "CaeManager.Infrastructure.DocumentosIa.PdfPigExtractorPalabrasPdf",
    ];

    /// <summary>Misma lista que <c>ConsumidoresDeIaConsultanLaInstruccionDeTratamientoTests.PuertosDeIa</c>.</summary>
    private static readonly string[] PuertosDeIa =
    [
        "IAsistenteIaService",
        "IDocumentAIRouterService",
        "IExtraccionMetadatosDocumentoIaService",
        "IExtraccionTrabajadoresIaService",
        "IDeteccionVisitaCorreoService",
        "IDeteccionGestionCorreoService",
        "IDeteccionRelevanciaCaeService",
        "IDeteccionIntencionCaeService",
        "IDocumentAIProviderFactory",
        "IDocumentAIProvider",
        "IDecisionesCerradasAsistenteService",
    ];

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.sln"))
               && !Directory.GetFiles(directorio.FullName, "*.slnx").Any())
            directorio = directorio.Parent;

        return directorio?.FullName ?? throw new InvalidOperationException("No se encuentra la raíz del repositorio.");
    }

    private static IEnumerable<(string Ruta, string Contenido)> Fuentes() =>
        DirectoriosDeLaHerramienta
            .SelectMany(d => Directory.GetFiles(Path.Combine(RaizDelRepositorio(), d), "*.cs", SearchOption.AllDirectories))
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    private static Type? Tipo(string nombreCompleto) =>
        AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(nombreCompleto)).FirstOrDefault(t => t is not null);

    private static void CargarEnsamblados()
    {
        foreach (var nombre in new[] { "CaeManager.Application", "CaeManager.Infrastructure" })
            ReflexionArquitecturaHelper.CargarAssembly(nombre);
    }

    private static bool EsDeIa(Type tipo)
    {
        var espacio = tipo.Namespace ?? string.Empty;
        return PuertosDeIa.Contains(tipo.Name)
            || espacio.Contains(".DocumentosIa", StringComparison.Ordinal)
            || espacio.Contains(".AsistenteIa", StringComparison.Ordinal)
            || espacio.StartsWith("Anthropic", StringComparison.Ordinal);
    }

    [Fact]
    public void Los_handlers_de_la_deteccion_no_reciben_ningun_puerto_de_ia()
    {
        CargarEnsamblados();

        var dependencias = ManejadoresDeLaDeteccion
            .Select(nombre => Tipo(nombre) ?? throw new InvalidOperationException($"No existe {nombre}: el ratchet se habría quedado ciego."))
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .ToList();

        dependencias.Should().NotBeEmpty();
        dependencias.Where(EsDeIa).Select(t => t.FullName).Should().BeEmpty(
            "en la detección ninguna página sale de TALVEG (decisión de Chris, 2026-11-01)");
    }

    [Fact]
    public void Las_implementaciones_locales_solo_reciben_un_logger()
    {
        CargarEnsamblados();

        var dependencias = ImplementacionesDeLaDeteccion
            .Select(nombre => Tipo(nombre) ?? throw new InvalidOperationException($"No existe {nombre}: el ratchet se habría quedado ciego."))
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType))
            .ToList();

        dependencias.Should().OnlyContain(t => t.Name.StartsWith("ILogger", StringComparison.Ordinal));
    }

    [Fact]
    public void Ningun_fichero_de_la_herramienta_nombra_un_puerto_ni_un_espacio_de_ia()
    {
        var fuentes = Fuentes().ToList();
        fuentes.Select(f => f.Ruta).Should().Contain(["DetectorTramosPdf.cs", "PrepararDivisionPdfQuery.cs", "PdfSharpDivisorPdf.cs"]);

        var patron = new Regex(
            $@"\b({string.Join('|', PuertosDeIa)})\b|\busing\s+CaeManager\.\w+\.(DocumentosIa|AsistenteIa)\b|IServiceProvider",
            RegexOptions.None, TimeSpan.FromSeconds(5));

        fuentes.Where(f => patron.IsMatch(f.Contenido)).Select(f => f.Ruta).Should().BeEmpty();
    }

    [Fact]
    public void Lo_unico_que_la_deteccion_pide_por_el_mediador_es_el_catalogo()
    {
        var patron = new Regex(@"mediator\.(Send|Publish)\(\s*(new\s+)?(?<peticion>\w+)", RegexOptions.None, TimeSpan.FromSeconds(5));

        var peticiones = Fuentes()
            .Where(f => f.Ruta != FicheroDelOrquestador)
            .SelectMany(f => patron.Matches(f.Contenido).Select(m => m.Groups["peticion"].Value))
            .ToList();

        peticiones.Should().NotBeEmpty("si no encuentra ninguna, el patrón ya no ve las llamadas");
        peticiones.Should().OnlyContain(p => p == "ObtenerCatalogoDivisionPdfQuery");
    }
}
