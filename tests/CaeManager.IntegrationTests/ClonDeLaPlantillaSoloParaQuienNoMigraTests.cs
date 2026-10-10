using System.Text.RegularExpressions;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Quién recibe un clon de la plantilla migrada y quién una base sin crear.
///
/// <para>
/// <c>BaseDatosPostgresDePruebas.CadenaConexionUnica()</c> entrega una base ya
/// migrada. Eso es correcto para el test que migra <b>para preparar</b>, y falso
/// para el test en que migrar <b>es lo que se prueba</b>: uno que maneje el
/// migrador (<c>IMigrator</c>, migrar hasta una migración concreta), que cree la
/// base por su cuenta o que mida el arranque. Sobre un clon, «migrar hasta la
/// migración N» desharía las posteriores en vez de partir de cero.
/// </para>
///
/// <para>
/// Dos barreras, ninguna dependiente de que alguien se acuerde: las carpetas
/// <c>Arranque/</c> y <c>Migraciones/</c> nunca reciben clon (lo decide
/// <see cref="BaseDatosPostgresDePruebas.MigraPorSiMismo"/> con el fichero que
/// llama), y fuera de ellas este test pone en rojo al fichero que pida un clon y
/// además maneje el migrador o cree la base.
/// </para>
/// </summary>
public partial class ClonDeLaPlantillaSoloParaQuienNoMigraTests
{
    private const string PideClon = "CadenaConexionUnica(";

    /// <summary>
    /// Ficheros fuera de <c>Arranque/</c> y <c>Migraciones/</c> cuyo objeto es la
    /// migración misma aunque no manejen el migrador: piden base sin crear.
    /// </summary>
    private static readonly string[] MigranComoObjetoDeLaPrueba = ["MigracionesTests.cs"];

    [Theory]
    [InlineData(@"C:\repo\tests\CaeManager.IntegrationTests\Arranque\ArnesDeArranqueRuntime.cs", true)]
    [InlineData(@"C:\repo\tests\CaeManager.IntegrationTests\Migraciones\MigracionesConcurrentesTrasBootstrapTests.cs", true)]
    [InlineData("/_/tests/CaeManager.IntegrationTests/Arranque/PilotoOutboundTests.cs", true)]
    [InlineData("/home/runner/work/Project-Hydra/tests/CaeManager.IntegrationTests/Migraciones/Sub/Algo.cs", true)]
    [InlineData("", true)]
    [InlineData(@"C:\otro\sitio\Fichero.cs", true)]
    [InlineData(@"C:\repo\tests\CaeManager.IntegrationTests\Tenants\AislamientoPorAgregadoTests.cs", false)]
    [InlineData("/_/tests/CaeManager.IntegrationTests/AlcancePorIdTests.cs", false)]
    [InlineData(@"C:\Arranque\Migraciones\tests\CaeManager.IntegrationTests\Tenants\Arranque.cs", false)]
    public void Arranque_y_Migraciones_nunca_reciben_clon_y_ante_la_duda_tampoco(string ficheroLlamante, bool migraPorSiMismo) =>
        BaseDatosPostgresDePruebas.MigraPorSiMismo(ficheroLlamante).Should().Be(migraPorSiMismo);

    [Fact]
    public async Task Fuera_de_esas_carpetas_la_base_llega_creada_y_con_todas_las_migraciones_y_dentro_no_existe()
    {
        var clon = BaseDatosPostgresDePruebas.CadenaConexionUnica();
        var desdeArranque = BaseDatosPostgresDePruebas.CadenaConexionUnica(
            ficheroLlamante: "/_/tests/CaeManager.IntegrationTests/Arranque/CualquierTest.cs");
        var vacia = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();
        try
        {
            (await ExisteAsync(clon)).Should().BeTrue();
            (await ExisteAsync(desdeArranque)).Should().BeFalse(
                "un test de Arranque/ crea y migra su base bajo la identidad que está midiendo");
            (await ExisteAsync(vacia)).Should().BeFalse();

            await using var conexion = new NpgsqlConnection(clon);
            await conexion.OpenAsync();
            await using var comando = new NpgsqlCommand("""SELECT count(*) FROM "__EFMigrationsHistory";""", conexion);
            var aplicadas = (long)(await comando.ExecuteScalarAsync())!;

            aplicadas.Should().Be(MigracionesDelEnsamblado(),
                "el clon trae aplicadas todas las migraciones, así que el MigrateAsync del test no tiene nada que aplicar");
        }
        finally
        {
            await BaseDatosPostgresDePruebas.EliminarAsync(clon);
        }
    }

    [Fact]
    public void Ningun_fichero_que_pida_un_clon_maneja_el_migrador_ni_crea_la_base_por_su_cuenta()
    {
        var ficheros = FicherosDeLaSuite();
        var quePidenClon = ficheros.Where(f => f.Texto.Contains(PideClon, StringComparison.Ordinal)).ToList();

        // Controles del instrumento: lee los fuentes de verdad, y los patrones
        // encuentran lo que hoy se sabe que hay en Migraciones/.
        quePidenClon.Count.Should().BeGreaterThan(200, "casi toda la suite prepara su base con CadenaConexionUnica");
        ficheros.Where(f => f.Ruta.StartsWith("Migraciones/", StringComparison.Ordinal) && MigraElMismo().IsMatch(f.Texto))
            .Should().HaveCountGreaterThanOrEqualTo(5, "los patrones tienen que reconocer a los tests de migración por pasos");

        var infractores = quePidenClon
            .Where(f => !BaseDatosPostgresDePruebas.MigraPorSiMismo($"/_/tests/CaeManager.IntegrationTests/{f.Ruta}"))
            .Where(f => MigraElMismo().IsMatch(f.Texto)
                        || MigranComoObjetoDeLaPrueba.Contains(f.Ruta, StringComparer.Ordinal))
            .Select(f => f.Ruta)
            .ToList();

        infractores.Should().BeEmpty(
            "un fichero que maneja el migrador (IMigrator, GetMigrator, MigrateAsync con destino, Migrate, EnsureCreated) "
            + "o crea la base (CREATE DATABASE) parte de una base sin crear: debe pedir CadenaConexionDeBaseVacia(), "
            + "no CadenaConexionUnica(), que entrega un clon ya migrado");
    }

    [GeneratedRegex(@"\bIMigrator\b|\bGetMigrator\b|\bMigrateAsync\(\s*[^)\s]|\.Migrate\(|\bEnsureCreated|CREATE\s+DATABASE", RegexOptions.IgnoreCase)]
    private static partial Regex MigraElMismo();

    private static int MigracionesDelEnsamblado() =>
        typeof(CaeManager.Migrations.PostgreSQL.ParticionadoMensualEventos).Assembly
            .GetTypes()
            .Count(tipo => tipo.IsSubclassOf(typeof(Microsoft.EntityFrameworkCore.Migrations.Migration)));

    private static async Task<bool> ExisteAsync(string cadena)
    {
        var nombre = new NpgsqlConnectionStringBuilder(cadena).Database;
        await using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @nombre;", conexion);
        comando.Parameters.AddWithValue("nombre", nombre!);
        return await comando.ExecuteScalarAsync() is not null;
    }

    private static List<(string Ruta, string Texto)> FicherosDeLaSuite()
    {
        var proyecto = Path.Combine(RaizDelRepositorio(), "tests", "CaeManager.IntegrationTests");
        var propio = nameof(ClonDeLaPlantillaSoloParaQuienNoMigraTests) + ".cs";

        return Directory.EnumerateFiles(proyecto, "*.cs", SearchOption.AllDirectories)
            .Select(ruta => Path.GetRelativePath(proyecto, ruta).Replace('\\', '/'))
            .Where(ruta => !ruta.StartsWith("bin/", StringComparison.Ordinal)
                           && !ruta.StartsWith("obj/", StringComparison.Ordinal)
                           && ruta != propio
                           && ruta != "BaseDatosPostgresDePruebas.cs")
            .Select(ruta => (ruta, SinComentarios(File.ReadAllLines(Path.Combine(proyecto, ruta)))))
            .ToList();
    }

    /// <summary>Un comentario que nombre <c>IMigrator</c> o un <c>CREATE DATABASE</c> no es usarlos.</summary>
    private static string SinComentarios(IEnumerable<string> lineas) =>
        string.Join('\n', lineas.Where(linea => !linea.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
            ?? throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }
}
