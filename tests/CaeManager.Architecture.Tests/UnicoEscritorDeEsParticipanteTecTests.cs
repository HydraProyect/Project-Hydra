using System.Text.RegularExpressions;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// D3 (decisiones de Chris del 2026-11-19): el marcador <c>Tenants.EsParticipanteTec</c> tiene un único escritor, el
/// comando de plataforma <c>MarcarTenantComoParticipanteTecCommand</c>, que lo escribe bajo
/// <c>cae_app_aprovisionamiento</c>. En la base lo garantiza el trigger <c>proteger_es_participante_tec</c> (lo prueba la
/// integración); aquí se congela el código: quién nombra el marcador, quién llama al mutador y que ningún seeder ni SQL a
/// mano fuera de la migración de D3 lo escribe. Mismo método que <see cref="UsosDeEsPlataformaCongeladosTests"/>: barrido
/// del árbol fuente, con los snapshots de EF excluidos por sufijo y nunca por carpeta.
/// </summary>
public class UnicoEscritorDeEsParticipanteTecTests
{
    private static readonly Regex Marcador = new(@"\bEsParticipanteTec\b|\bMarcarComoParticipanteTec\b", RegexOptions.Compiled);

    /// <summary>Llamadas al mutador (no su declaración, que va precedida de <c>void</c>).</summary>
    private static readonly Regex LlamadaAlMutador = new(@"\.MarcarComoParticipanteTec\s*\(", RegexOptions.Compiled);

    /// <summary>Asignaciones a la propiedad (<c>=</c> que no es <c>==</c> ni <c>=&gt;</c>).</summary>
    private static readonly Regex AsignacionALaPropiedad = new(@"\bEsParticipanteTec\s*=(?![=>])", RegexOptions.Compiled);

    /// <summary>Asignaciones a la propiedad de un valor que no es el literal <c>false</c>.</summary>
    private static readonly Regex AsignacionQueNoEsFalse = new(@"\bEsParticipanteTec\s*=(?![=>])(?>\s*)(?!false\b)", RegexOptions.Compiled);

    private const string Migracion = "src/CaeManager.Migrations.PostgreSQL/Migrations/20261119003026_AnadeEsParticipanteTecATenants.cs";
    private const string Comando = "src/CaeManager.Application/Tenants/Commands/MarcarParticipanteTec/MarcarTenantComoParticipanteTecCommand.cs";
    private const string Entidad = "src/CaeManager.Domain/Tenants/Tenant.cs";

    /// <summary>
    /// Todo fichero de <c>src</c> que nombra el marcador. Uno nuevo tiene que verse aquí: un lector más es una decisión de
    /// D3 (qué cierra), un escritor más rompe la regla del único escritor.
    /// </summary>
    private static readonly string[] FicherosQueNombranElMarcador =
    [
        Entidad,
        "src/CaeManager.Infrastructure/Persistence/Configurations/TenantConfiguration.cs",
        Migracion,
        Comando,
        // El lector de D3.
        "src/CaeManager.Application/Comercial/LineaOutbound/OutboundCerradoTenantTec.cs",
    ];

    [Fact]
    public void Solo_los_ficheros_inventariados_nombran_el_marcador()
    {
        var (conteos, universo) = Escanear(Marcador);

        universo.Should().Contain(Entidad, "el barrido tiene que haber visto la entidad para que su resultado signifique algo");
        conteos.Keys.Should().BeEquivalentTo(FicherosQueNombranElMarcador,
            "un fichero nuevo que lee o escribe EsParticipanteTec se revisa aquí (D3: único escritor)");
    }

    [Fact]
    public void Solo_el_comando_de_plataforma_llama_al_mutador()
    {
        var (conteos, _) = Escanear(LlamadaAlMutador);

        conteos.Should().BeEquivalentTo(new Dictionary<string, int> { [Comando] = 1 },
            "MarcarTenantComoParticipanteTecCommand es el único escritor del marcador");
    }

    [Fact]
    public void Solo_la_entidad_asigna_la_propiedad()
    {
        var (conteos, _) = Escanear(AsignacionQueNoEsFalse);

        conteos.Keys.Should().BeEquivalentTo([Entidad], "fuera de Tenant nadie pone EsParticipanteTec a un valor que no sea false");
    }

    [Fact]
    public void La_unica_otra_asignacion_es_el_false_del_Tenant_de_plataforma_en_HasData()
    {
        // La siembra de EF del Tenant #1 (TenantConfiguration, HasData) fija EsParticipanteTec = false: deja la fila de
        // plataforma sin marcar, así que no es un escritor del marcador. Cualquier otra asignación rompe la regla.
        var (conteos, _) = Escanear(AsignacionALaPropiedad);

        conteos.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            [Entidad] = conteos.GetValueOrDefault(Entidad),
            ["src/CaeManager.Infrastructure/Persistence/Configurations/TenantConfiguration.cs"] = 1,
        });
        conteos.GetValueOrDefault(Entidad).Should().BeGreaterThan(0, "la entidad es quien asigna el marcador");
    }

    [Fact]
    public void Ningun_seeder_ni_script_escribe_el_marcador()
    {
        var raiz = RaizDelRepositorio();
        var encontrados = new List<string>();
        foreach (var carpeta in new[] { "src", "scripts", "deploy" })
        {
            var ruta = Path.Combine(raiz, carpeta);
            if (!Directory.Exists(ruta)) continue;
            foreach (var archivo in Directory.EnumerateFiles(ruta, "*", SearchOption.AllDirectories))
            {
                if (!NoEstaEnBinNiObj(archivo) || EsGenerado(archivo)) continue;
                var relativa = Relativa(raiz, archivo);
                if (relativa == Migracion) continue;
                if (!Path.GetFileName(archivo).Contains("Seed", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetExtension(archivo).Equals(".sql", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetExtension(archivo).Equals(".sh", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetExtension(archivo).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (Marcador.IsMatch(File.ReadAllText(archivo)))
                    encontrados.Add(relativa);
            }
        }

        encontrados.Should().BeEmpty("ningún seeder ni guion pone el marcador: solo el comando de plataforma (D3)");
    }

    [Fact]
    public void La_guarda_de_D3_esta_en_el_pipeline_tras_el_papel_y_antes_de_la_elevacion()
    {
        var servicios = new ServiceCollection();
        servicios.AddApplication();

        var behaviors = servicios.Where(d => d.ServiceType == typeof(IPipelineBehavior<,>)).Select(d => d.ImplementationType).ToList();
        var limite = behaviors.IndexOf(typeof(LimiteTenantTecBehavior<,>));
        limite.Should().BeGreaterThanOrEqualTo(0, "la guarda de D3 tiene que estar registrada");
        limite.Should().BeGreaterThan(behaviors.IndexOf(typeof(GuardaPorServicioBehavior<,>)));
        limite.Should().BeGreaterThan(behaviors.IndexOf(typeof(AutorizacionEscrituraBehavior<,>)));
        limite.Should().BeLessThan(behaviors.IndexOf(typeof(ValidationBehavior<,>)), "antes de llegar al handler");
        limite.Should().BeLessThan(behaviors.IndexOf(typeof(ElevacionEscrituraAprovisionamientoBehavior<,>)),
            "cae_app_aprovisionamiento no ve la línea Outbound: la guarda lee antes de elevar");
    }

    private static (Dictionary<string, int> Conteos, List<string> Universo) Escanear(Regex patron)
    {
        var raiz = RaizDelRepositorio();
        var conteos = new Dictionary<string, int>(StringComparer.Ordinal);
        var universo = new List<string>();

        foreach (var archivo in Directory.EnumerateFiles(Path.Combine(raiz, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (!NoEstaEnBinNiObj(archivo)) continue;
            var relativa = Relativa(raiz, archivo);
            universo.Add(relativa);
            if (EsGenerado(archivo)) continue;

            var apariciones = patron.Matches(File.ReadAllText(archivo)).Count;
            if (apariciones > 0) conteos[relativa] = apariciones;
        }

        return (conteos, universo);
    }

    private static string Relativa(string raiz, string archivo) =>
        Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');

    private static bool EsGenerado(string archivo)
    {
        var nombre = Path.GetFileName(archivo);
        return nombre.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
               || nombre.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NoEstaEnBinNiObj(string archivo) =>
        !archivo.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        && !archivo.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory + ": este test necesita el árbol fuente.");
    }
}
