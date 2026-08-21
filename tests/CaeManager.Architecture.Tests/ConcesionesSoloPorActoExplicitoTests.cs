using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>No hay bootstrap silencioso de privilegios.</b> Una
/// <c>ConcesionPrivilegio</c> solo puede nacer de un acto explícito de
/// concesión, nunca de un proceso que las fabrique al arrancar o de paso.
///
/// La propiedad se vigila por <b>puntos de escritura</b>, no por nombres. Un
/// ratchet que buscara la palabra <c>Seeder</c> pasaría en verde ante cualquier
/// otro mecanismo de bootstrap —un hosted service, una migración con datos, un
/// endpoint de mantenimiento— y protegería una forma sintáctica en vez de la
/// arquitectura. Aquí se persiguen las dos únicas maneras de traer una concesión
/// al mundo: sus fábricas de dominio y su inserción en el contexto.
///
/// <para>
/// <b>Por qué importa.</b> La vía heredada de soporte funciona al revés: un
/// seeder pre-aprovisiona una <c>DelegacionTenant</c> inactiva hacia <i>todos</i>
/// los tenants, y lo que se controla después es la activación. Trasladar ese
/// modelo al plano 3 crearía miles de relaciones de privilegio potencial
/// distinguidas artificialmente por su estado, justo cuando F2b-5 estableció que
/// una fila de concesión <b>es</b> lo que nombra a un usuario en el plano de
/// privilegio. Sin fila, no hay relación; y eso es más limpio que "hay fila pero
/// no cuenta".
/// </para>
///
/// <para>
/// <b>Alcance deliberadamente estrecho.</b> Esto no prohíbe que en el futuro
/// exista una operación que cree concesiones — al contrario, la lista de
/// autorizados está para eso. Protege el contrato de HOY: F2b-6 no introduce
/// pre-aprovisionamiento. Cuando la migración a capacidades cambie el origen de
/// una concesión, se modifica esta lista <i>junto con</i> ese contrato, y se ve
/// en la revisión.
/// </para>
/// </summary>
public class ConcesionesSoloPorActoExplicitoTests
{
    /// <summary>
    /// Las dos formas de crear una concesión. El constructor es privado, así que
    /// las fábricas son la única puerta del dominio; y <c>Add</c> sobre el DbSet
    /// es la única de persistencia.
    /// </summary>
    private static readonly Regex PatronCreacion = new(
        @"ConcesionPrivilegio\.SobreTenants\(|ConcesionPrivilegio\.Global\(" +
        @"|ConcesionesPrivilegio\.Add(Range|Async)?\(",
        RegexOptions.Compiled);

    /// <summary>
    /// Puntos de creación autorizados. <b>Exactamente uno</b>, y el nombre del
    /// archivo ya dice quién autoriza y a nombre de quién se crea.
    /// </summary>
    private static readonly Dictionary<string, string> PuntosDeCreacionAutorizados = new()
    {
        ["src/CaeManager.Application/Plataforma/Commands/AutoConcederPrivilegio/AutoConcederPrivilegioCommand.cs"] =
            "auto-concesión: el beneficiario no es un parámetro, sale de la sesión, así que solo puede crear " +
            "concesiones a nombre de quien ejecuta. Autorizada por la misma puerta de plataforma que la apertura " +
            "y con 2FA. ADR-011 § 4bis.7.7 la admite mientras el equipo sea unipersonal.",

        // El escritor recibe el agregado ya construido: no puede fabricar una
        // concesión, solo persistir la que le den.
        ["src/CaeManager.Infrastructure/Plataforma/PlataformaWriter.cs"] =
            "persiste el agregado que le entregan; no lo construye.",
    };

    [Fact]
    public void Ningun_codigo_de_produccion_crea_concesiones_de_privilegio()
    {
        var raiz = RaizDelRepositorio();
        var carpetas = new[]
        {
            "src/CaeManager.Application", "src/CaeManager.Infrastructure", "src/CaeManager.Web",
        };

        var infractores = new List<string>();

        foreach (var carpeta in carpetas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));

            foreach (var archivo in Directory.EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories))
            {
                var rutaRelativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');
                if (PuntosDeCreacionAutorizados.ContainsKey(rutaRelativa)) continue;

                if (File.ReadLines(archivo).Any(linea => PatronCreacion.IsMatch(linea)))
                    infractores.Add(rutaRelativa);
            }
        }

        string.Join("\n", infractores.OrderBy(x => x)).Should().BeEmpty(
            "una concesión de privilegio solo puede nacer de un acto explícito de concesión: si este código la " +
            "crea de otra forma —al arrancar, en un job, en una migración con datos— estaría haciendo bootstrap " +
            "silencioso de privilegios. Si el punto de creación es legítimo, añádelo a PuntosDeCreacionAutorizados " +
            "en este mismo commit explicando quién autoriza esa concesión y a nombre de quién se crea");
    }

    [Fact]
    public void Cada_punto_de_creacion_autorizado_explica_quien_autoriza()
    {
        // NotContain y no OnlyContain: hoy la lista está vacía y eso es lo
        // correcto. El día que tenga una entrada, este test exige que traiga su
        // motivo escrito.
        PuntosDeCreacionAutorizados.Should().NotContain(
            e => string.IsNullOrWhiteSpace(e.Value),
            "conceder un privilegio de plataforma sin dejar escrito quién lo autoriza es exactamente el bootstrap " +
            "silencioso que este test existe para impedir");
    }

    /// <summary>
    /// Guarda del propio ratchet: si el patrón dejara de encontrar las
    /// creaciones que sí existen en los tests, estaría vigilando algo que ya no
    /// se escribe así, y su verde no significaría nada.
    /// </summary>
    [Fact]
    public void El_patron_reconoce_las_creaciones_que_existen_en_los_tests()
    {
        var raiz = RaizDelRepositorio();
        var directorio = Path.Combine(raiz, "tests");

        var archivosQueCrean = Directory
            .EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Count(a => File.ReadLines(a).Any(l => PatronCreacion.IsMatch(l)));

        archivosQueCrean.Should().BeGreaterThan(2,
            "los tests del plano 3 sí crean concesiones; si el patrón no las ve, tampoco vería una creación " +
            "nueva en código de producción");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}
