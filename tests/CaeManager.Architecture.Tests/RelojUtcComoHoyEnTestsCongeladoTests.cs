using System.Text.RegularExpressions;
using CaeManager.Domain.Common;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Los tests de <c>tests/CaeManager.E2ETests</c> y <c>tests/CaeManager.IntegrationTests</c> no
/// sacan «hoy» del reloj UTC ni del reloj local del servidor: el día de negocio es
/// <see cref="DiaDeNegocio.Hoy"/> (Europe/Madrid, decisión de producto 2026-09-28) y
/// <see cref="DiaDeNegocioUnicaFuenteTests"/> solo vigila <c>src/</c>.
///
/// <para>
/// <b>Por qué existe.</b> Entre las 22:00 y las 24:00 UTC en verano el día de Madrid ya es el
/// siguiente al día UTC. Tres tests que usaban el reloj UTC como «hoy» (dos E2E y un UPDATE con
/// <c>CURRENT_DATE</c> en un test de RLS) fallaron dentro de esa ventana y cortaron la cola de
/// fusión de una noche entera; un test que pasa de día y falla de noche no lo detecta ninguna
/// ejecución diurna. Este trinquete impide que el patrón vuelva a crecer en <c>tests/</c>.
/// </para>
///
/// <para>
/// <b>Qué cuenta</b> (por fichero, sin comentarios —<see cref="LimpiadorDeComentarios"/>—, con
/// las cadenas intactas): el día actual sacado del reloj UTC o del local del servidor, es decir
/// <c>DateOnly.FromDateTime(DateTime.UtcNow)</c> y variantes con <c>Now</c>, <c>Today</c> o
/// <c>GetUtcNow()</c>, <c>UtcNow.Date</c>, <c>DateTime.Today</c>, <c>DateTime.Now.Date</c>, y en SQL dentro
/// de literales <c>CURRENT_DATE</c> y <c>now()::date</c> (con o sin <c>+ interval '…'</c>).
/// <b>No cuenta</b> <c>DateTime.UtcNow</c> a secas: un instante (espera, timeout, marca de tiempo
/// <c>UtcNow - N días</c>) no es el «hoy» de negocio.
/// </para>
///
/// <para>
/// <b>Deuda congelada, no migrada.</b> <see cref="Congelado"/> fija el recuento actual por
/// fichero y falla por <b>igualdad sin tolerancia</b>: si un fichero sube (o aparece uno nuevo)
/// se ha escrito otro «hoy» de reloj; si baja, se ha migrado y hay que bajar la cifra en el mismo
/// commit, para que solo pueda bajar. La lista salió de buscar el patrón, no de medir cada flujo:
/// que un fichero figure aquí no prueba que falle en la ventana, solo que podría.
/// </para>
///
/// <para>
/// <b>Qué usar en su lugar.</b> En E2E, <c>Ayudas.HoyDeNegocio()</c>
/// (<c>tests/CaeManager.E2ETests/Ayudas.cs</c>, el proyecto no referencia Domain); en Integration
/// y Application, <see cref="DiaDeNegocio.Hoy"/>. En SQL, pasa la fecha como parámetro en vez de
/// <c>CURRENT_DATE</c>.
/// </para>
/// </summary>
public class RelojUtcComoHoyEnTestsCongeladoTests
{
    private static readonly string[] Carpetas =
        ["tests/CaeManager.E2ETests", "tests/CaeManager.IntegrationTests"];

    /// <summary>
    /// Ocurrencias por fichero. Solo baja. Recuento medido con el mismo patrón y el mismo
    /// limpiador sobre el árbol real.
    /// </summary>
    private static readonly Dictionary<string, int> Congelado = new(StringComparer.Ordinal)
    {
        ["tests/CaeManager.E2ETests/BucleCorreccionPlataformaTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/CentrosGestionarEnVivoE2ETests.cs"] = 1,
        ["tests/CaeManager.E2ETests/FlujoBandejaPriorizadaTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/FlujoCicloDocumentalTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/FlujoCriticoTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/IdiomaPorCuentaTests.cs"] = 3,
        ["tests/CaeManager.E2ETests/ImportacionTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/ImportarDocumentosTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/SubidaMasivaTests.cs"] = 1,
        ["tests/CaeManager.E2ETests/VisitaCentroGestionadoPorCorreoE2ETests.cs"] = 3,
        // Horizonte de particiones de auditoría (current_date + 3650), no un «hoy» de negocio:
        // se congela porque el patrón es el mismo y no distingue.
        ["tests/CaeManager.IntegrationTests/Auditoria/ParticionadoAuditoriaBajoRuntimeTests.cs"] = 1,
        ["tests/CaeManager.IntegrationTests/Importacion/ClosedXmlImportacionParserTests.cs"] = 3,
        ["tests/CaeManager.IntegrationTests/Importacion/ClosedXmlPlantillaDocumentosServiceTests.cs"] = 4,
        // INSERT que la RLS debe rechazar: la fecha no interviene en lo que se prueba.
        ["tests/CaeManager.IntegrationTests/Tenants/AislamientoRlsCentroYSatelitesTests.cs"] = 1,
    };

    private static readonly Regex PatronHoyDesdeReloj = new(
        @"\bDateOnly\s*\.\s*FromDateTime\s*\(\s*(?:System\s*\.\s*)?DateTime(?:Offset)?\s*\.\s*(?:UtcNow|Now|Today)\b"
        + @"|\bDateOnly\s*\.\s*FromDateTime\s*\([^;]*?GetUtcNow\s*\("
        + @"|\bUtcNow\s*(?:\(\s*\))?\s*\.\s*Date\b"
        + @"|\bUtcDateTime\s*\.\s*Date\b"
        + @"|\bDateTime\s*\.\s*Now\s*\.\s*Date\b"
        + @"|\bDateTime\s*\.\s*Today\b"
        + @"|\bCURRENT_DATE\b"
        + @"|\b(?:now|current_timestamp)\s*(?:\(\s*\))?(?:\s*[+\-]\s*interval\s*'[^']*')?\s*\)?\s*::\s*date\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    [Fact]
    public void Los_tests_no_sacan_hoy_del_reloj_utc_ni_del_del_servidor_mas_alla_de_la_deuda_congelada()
    {
        var medido = ContarPorFichero(RaizDelRepositorio());

        Divergencias(Congelado, medido).Should().BeEmpty(
            "«hoy» en un test es el día de negocio (Ayudas.HoyDeNegocio() en E2E, DiaDeNegocio.Hoy() en " +
            "Integration), no el reloj UTC: entre las 22:00 y las 24:00 UTC en verano difieren y el test " +
            "rompe la cola de fusión. Si has migrado un uso, baja su recuento en Congelado (solo puede bajar)");
    }

    [Fact]
    public void El_detector_cuenta_las_formas_que_vigila_e_ignora_instantes_y_comentarios()
    {
        string[] cuentan =
        [
            "var hoy = DateOnly.FromDateTime(DateTime.UtcNow);",
            "var hoy = DateOnly.FromDateTime(System.DateTime.UtcNow);",
            "var hoy = DateOnly.FromDateTime(DateTime.Now);",
            "var hoy = DateOnly.FromDateTime(DateTime.Today);",
            "var hoy = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);",
            "var hoy = DateOnly.FromDateTime(reloj.GetUtcNow().UtcDateTime);",
            "var d = DateTime.UtcNow.Date.AddDays(-30);",
            "var d = DateTimeOffset.UtcNow.Date;",
            "var d = DateTime.Today.AddMonths(1);",
            "var d = DateTime.Now.Date;",
            "var sql = \"UPDATE \\\"Asignaciones\\\" SET \\\"Desde\\\" = CURRENT_DATE\";",
            "var sql = \"SELECT current_date + 3650\";",
            "var sql = \"SELECT now()::date\";",
            "var sql = \"SELECT (now() + interval '3 days')::date\";",
        ];
        foreach (var linea in cuentan)
            Contar(linea).Should().Be(1, linea);

        Contar("var sql = \"SELECT (now() + interval '3 days')::date, (now() + interval '3 days')::date\";")
            .Should().Be(2, "cada expresión cuenta una vez");

        string[] noCuentan =
        [
            "var ahora = DateTime.UtcNow;",
            "var vence = DateTime.UtcNow.AddDays(-30);",
            "var espera = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);",
            "var hoy = DiaDeNegocio.Hoy();",
            "var hoy = Ayudas.HoyDeNegocio();",
            "var fecha = DateOnly.FromDateTime(celda.GetDateTime());",
            "var fecha = DateOnly.FromDateTime(desde);",
            "public override DateTimeOffset GetUtcNow() => ahora;",
            "var sql = \"SELECT now()\";",
            "var sql = \"SELECT @hoy::date\";",
            "// antes: DateOnly.FromDateTime(DateTime.UtcNow) y CURRENT_DATE",
            "/// <see cref=\"DateTime.Today\"/> es el día del servidor",
            "/* DateTime.UtcNow.Date */ var x = 1;",
            "var x = 1; // DateTime.Today",
        ];
        foreach (var linea in noCuentan)
            Contar(linea).Should().Be(0, linea);
    }

    [Fact]
    public void Las_divergencias_se_describen_con_el_fichero_y_la_direccion_del_cambio()
    {
        var esperado = new Dictionary<string, int> { ["a.cs"] = 2, ["b.cs"] = 1 };
        var medido = new Dictionary<string, int> { ["a.cs"] = 3, ["b.cs"] = 0, ["c.cs"] = 1 };

        var mensajes = Divergencias(esperado, medido);

        mensajes.Should().HaveCount(3);
        mensajes.Should().Contain(m => m.Contains("a.cs") && m.Contains("usa Ayudas.HoyDeNegocio() / DiaDeNegocio.Hoy()"));
        mensajes.Should().Contain(m => m.Contains("b.cs") && m.Contains("baja el número congelado"));
        mensajes.Should().Contain(m => m.Contains("c.cs") && m.Contains("usa Ayudas.HoyDeNegocio() / DiaDeNegocio.Hoy()"));
    }

    private static List<string> Divergencias(Dictionary<string, int> esperado, Dictionary<string, int> medido) =>
        esperado.Keys.Union(medido.Keys)
            .Select(ruta => (Ruta: ruta, Esperado: esperado.GetValueOrDefault(ruta), Medido: medido.GetValueOrDefault(ruta)))
            .Where(x => x.Esperado != x.Medido)
            .OrderBy(x => x.Ruta, StringComparer.Ordinal)
            .Select(x => x.Medido > x.Esperado
                ? $"{x.Ruta}: {x.Medido} usos del reloj UTC/local como «hoy» (congelados {x.Esperado}); " +
                  "usa Ayudas.HoyDeNegocio() / DiaDeNegocio.Hoy(), no el reloj UTC"
                : $"{x.Ruta}: {x.Medido} usos (congelados {x.Esperado}); has migrado alguno: baja el número congelado a {x.Medido}")
            .ToList();

    private static int Contar(string texto) =>
        PatronHoyDesdeReloj.Matches(LimpiadorDeComentarios.Quitar(texto, razor: false)).Count;

    private static Dictionary<string, int> ContarPorFichero(string raiz)
    {
        var sep = Path.DirectorySeparatorChar;
        var ficheros = new List<string>();
        foreach (var carpeta in Carpetas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', sep));
            Directory.Exists(directorio).Should().BeTrue($"si {carpeta} cambia de sitio, este trinquete deja de vigilar nada");
            var deCarpeta = Directory.EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}")
                            && !f.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                            && !f.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                .ToList();
            deCarpeta.Should().NotBeEmpty($"{carpeta} tiene que contener tests que vigilar");
            ficheros.AddRange(deCarpeta);
        }

        return ficheros
            .Select(f => (Ruta: Path.GetRelativePath(raiz, f).Replace(sep, '/'), Cuenta: Contar(File.ReadAllText(f))))
            .Where(x => x.Cuenta > 0)
            .ToDictionary(x => x.Ruta, x => x.Cuenta, StringComparer.Ordinal);
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        actual.Should().NotBeNull("los tests tienen que correr dentro del repositorio");
        return actual!.FullName;
    }
}
