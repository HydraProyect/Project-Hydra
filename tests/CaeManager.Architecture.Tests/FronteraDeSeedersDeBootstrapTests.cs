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

    [Fact]
    public void Solo_los_dos_seeders_administrativos_usan_el_contexto_de_bootstrap()
    {
        var programa = File.ReadAllText(Path.Combine(
            RaizDelRepositorio(), "src", "CaeManager.Web", "Program.cs"));

        var conBootstrap = new Regex(@"await (\w+Seeder)\.SeedAsync\([^;]*dbContextBootstrap", RegexOptions.Compiled)
            .Matches(programa).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n).ToList();

        var conInyectado = new Regex(@"await (\w+Seeder)\.SeedAsync\((?![^;]*dbContextBootstrap)[^;]*dbContext",
                RegexOptions.Compiled)
            .Matches(programa).Select(m => m.Groups[1].Value).Distinct().OrderBy(n => n).ToList();

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
