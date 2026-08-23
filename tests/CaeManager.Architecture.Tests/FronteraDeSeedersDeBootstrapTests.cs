using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Qué seeders cruzan la frontera de identidad, y cuáles no.</b>
///
/// <para>
/// El arranque siembra con <b>dos</b> identidades distintas: la administrativa
/// (<c>dbContextBootstrap</c>, rol propietario) y la de tráfico normal
/// (<c>dbContext</c>, <c>cae_app_runtime</c> cuando está configurado). Cuál usa
/// cada seeder no es un detalle de fontanería: es la diferencia entre arrancar y
/// no arrancar.
/// </para>
///
/// <para>
/// Lo aprendimos en staging el 2026-08-23, en cuanto ese entorno adoptó el rol
/// restringido: <c>IdentitySeeder</c> murió con <c>23505</c> porque su lectura de
/// <c>EstadoBootstrapPlataforma</c> quedaba filtrada por RLS y la guarda "si no
/// existe, créala" entraba siempre. El backfill de asignaciones habría fallado
/// después por el motivo contrario —escribe una fila por tenant y la política
/// exige <c>PropietarioTenantId = app.tenant_id</c>—, aunque allí no llegó a
/// manifestarse porque sus filas ya existían.
/// </para>
///
/// <para>
/// <b>La dirección peligrosa es moverlos hacia el contexto administrativo.</b> Un
/// seeder tenant-scoped que pasara a sembrar con el rol propietario dejaría de
/// estar sujeto a RLS sin que ninguna aserción de comportamiento lo notara: sus
/// tests seguirían en verde. Por eso la lista es cerrada en los dos sentidos.
/// </para>
/// </summary>
public class FronteraDeSeedersDeBootstrapTests
{
    /// <summary>
    /// Los únicos dos, con el motivo por el que no son tráfico de aplicación.
    /// </summary>
    private static readonly Dictionary<string, string> ConIdentidadAdministrativa = new()
    {
        ["IdentitySeeder"] =
            "escribe EstadoBootstrapPlataforma, que es estado de SISTEMA: su política de SELECT exige " +
            "app.usuario_id y en el arranque no hay sesión de usuario",
        ["AsignacionesOperativasBackfillSeeder"] =
            "es cross-tenant por diseño —lo declara al justificar IgnoreQueryFilters— y escribe una fila " +
            "por cada tenant en un solo SaveChanges",
    };

    /// <summary>
    /// Toda invocación de un seeder desde el arranque, con el texto de sus
    /// argumentos: la unidad sobre la que se decide la identidad.
    /// </summary>
    private static readonly Regex Invocacion = new(
        @"await\s+(\w+Seeder)\.\w+\(([^;]*)\)", RegexOptions.Compiled);

    private sealed record Llamada(string Seeder, string Argumentos);

    private static List<Llamada> LlamadasDelArranque()
    {
        var programa = File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        return Invocacion.Matches(programa)
            .Select(m => new Llamada(m.Groups[1].Value, m.Groups[2].Value))
            .ToList();
    }

    private static bool UsaBootstrap(Llamada llamada) =>
        Regex.IsMatch(llamada.Argumentos, @"\bdbContextBootstrap\b");

    private static bool UsaInyectado(Llamada llamada) =>
        !UsaBootstrap(llamada) && Regex.IsMatch(llamada.Argumentos, @"\bdbContext\b");

    /// <summary>
    /// <b>Cada llamada declara su identidad en el propio sitio de llamada.</b>
    ///
    /// <para>
    /// Repara un falso negativo demostrado por mutación, y de la clase de
    /// regresión más grave que este ratchet dice impedir. Bastaba un alias para
    /// que un seeder tenant-scoped pasara al rol propietario sin que nada lo
    /// notara:
    /// </para>
    /// <code>
    /// var contextoAdministrativo = dbContextBootstrap;
    /// await DelegacionesSoporteSeeder.SeedAsync(contextoAdministrativo, …);
    /// </code>
    /// <para>
    /// Esa llamada no contiene <c>dbContextBootstrap</c> —así que no entraba en
    /// la lista administrativa— ni <c>dbContext</c> —así que tampoco en la de
    /// tráfico normal—. Caía <b>fuera de las dos listas</b>, y las tres
    /// aserciones de abajo seguían pasando: la equivalencia porque la lista
    /// administrativa no cambiaba, la de no vacío porque quedaban otros seeders
    /// inyectados, y la de intersección porque no había ninguna. El seeder
    /// sembraba fuera de RLS con el ratchet en verde.
    /// </para>
    ///
    /// <para>
    /// La propiedad no es "estas dos listas contienen lo que deben": es que
    /// <b>toda</b> llamada pertenezca a una de las dos. Una llamada que no se
    /// pueda clasificar leyendo su sitio de llamada es, por sí sola, el defecto
    /// —da igual a qué contexto acabe apuntando el alias—, porque la frontera de
    /// identidad deja de ser legible justo donde se cruza.
    /// </para>
    /// </summary>
    [Fact]
    public void Toda_llamada_a_un_seeder_declara_su_identidad_en_el_sitio_de_llamada()
    {
        var llamadas = LlamadasDelArranque();

        llamadas.Should().NotBeEmpty(
            "el arranque siembra; una lista vacía significaría que el patrón ya no reconoce las " +
            "invocaciones que este test dice vigilar");

        var sinClasificar = llamadas
            .Where(l => !UsaBootstrap(l) && !UsaInyectado(l))
            .Select(l => $"{l.Seeder}({l.Argumentos.Trim()})")
            .OrderBy(x => x)
            .ToList();

        string.Join(Environment.NewLine, sinClasificar).Should().BeEmpty(
            "cada seeder tiene que recibir dbContextBootstrap o dbContext de forma visible en su propia " +
            "llamada; pasarlo por un alias esconde qué identidad —y por tanto qué régimen de RLS— usa esa " +
            "siembra, que es justo lo que este ratchet existe para mantener a la vista");
    }

    [Fact]
    public void Solo_los_dos_seeders_administrativos_usan_el_contexto_de_bootstrap()
    {
        var llamadas = LlamadasDelArranque();

        var conBootstrap = llamadas.Where(UsaBootstrap).Select(l => l.Seeder).Distinct().OrderBy(n => n).ToList();
        var conInyectado = llamadas.Where(UsaInyectado).Select(l => l.Seeder).Distinct().OrderBy(n => n).ToList();

        // Guarda del instrumento: si el patrón dejara de reconocer las llamadas,
        // las dos listas saldrían vacías y el test pasaría sin observar nada.
        conInyectado.Should().NotBeEmpty(
            "el arranque tiene que seguir sembrando algo con el contexto inyectado; dos listas vacías " +
            "significarían que este test ya no ve las llamadas que dice vigilar");

        conBootstrap.Should().BeEquivalentTo(ConIdentidadAdministrativa.Keys,
            "mover un seeder a la identidad administrativa lo saca de RLS sin que ninguna aserción de " +
            "comportamiento lo note: sus tests seguirían verdes. Añadir uno aquí exige escribir por qué " +
            "no es tráfico de aplicación, en el mismo commit");

        conBootstrap.Should().NotIntersectWith(conInyectado,
            "un mismo seeder con las dos identidades sería ambiguo: la mitad de sus escrituras estaría " +
            "sujeta a RLS y la otra mitad no");
    }

    [Fact]
    public void Cada_seeder_administrativo_declara_por_que_lo_es()
    {
        ConIdentidadAdministrativa.Should().NotContain(
            e => string.IsNullOrWhiteSpace(e.Value),
            "salir de RLS es una decisión de seguridad, y una decisión de seguridad sin motivo escrito " +
            "es un descuido que nadie podrá revisar después");
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
