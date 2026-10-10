using CaeManager.Application.Common;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// La migración <c>AnadeBusquedaSinAcentosEnListados</c>: la extensión <c>unaccent</c>, las dos
/// funciones de búsqueda y los índices trigram sobre la expresión nueva; y su vuelta atrás.
/// </summary>
public class AnadeBusquedaSinAcentosEnListadosTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261009205856_AnadeBusquedaSinAcentosEnListados";

    private static readonly string[] IndicesNuevos =
    [
        "IX_Centros_Nombre_Busqueda", "IX_Empresas_RazonSocial_Busqueda",
        "IX_Trabajadores_Alias_Busqueda", "IX_Trabajadores_Apellidos_Busqueda",
        "IX_Trabajadores_Dni_Busqueda", "IX_Trabajadores_Nombre_Busqueda",
        "IX_Vehiculos_Modelo_Busqueda", "IX_Vehiculos_Nombre_Busqueda", "IX_Vehiculos_NumeroPlaca_Busqueda",
    ];

    /// <summary>Los que solo servían a los listados de Trabajadores y de Vehículos: la migración los retira.</summary>
    private static readonly string[] IndicesRetirados =
    [
        "IX_Trabajadores_Alias_Trgm", "IX_Trabajadores_Dni_Trgm",
        "IX_Vehiculos_Modelo_Trgm", "IX_Vehiculos_Nombre_Trgm", "IX_Vehiculos_NumeroPlaca_Trgm",
    ];

    /// <summary>Los que siguen sirviendo al buscador global y a los listados que comparan con <c>upper(columna)</c>.</summary>
    private static readonly string[] IndicesConservados =
    [
        "IX_Centros_Nombre_Trgm", "IX_Empresas_RazonSocial_Trgm",
        "IX_Trabajadores_Apellidos_Trgm", "IX_Trabajadores_Nombre_Trgm",
    ];

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public Task InitializeAsync() => BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Tras_migrar_existen_la_extension_las_dos_funciones_y_los_indices_sobre_la_expresion_nueva()
    {
        (await EscalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'unaccent'")).Should().Be(1);

        // Inmutables y seguras en paralelo: sin eso la primera no se puede indexar.
        (await EscalarAsync<string>(
            """
            SELECT string_agg(proname || ':' || provolatile::text || proparallel::text, ',' ORDER BY proname)
            FROM pg_proc
            WHERE pronamespace = 'public'::regnamespace AND proname IN ('texto_de_busqueda', 'patron_de_busqueda')
            """)).Should().Be("patron_de_busqueda:is,texto_de_busqueda:is");

        (await IndicesExistentesAsync(IndicesNuevos)).Should().BeEquivalentTo(IndicesNuevos);
        (await EscalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = ANY(@nombres) AND indexdef LIKE '%texto_de_busqueda(%gin_trgm_ops%'",
            ("nombres", IndicesNuevos))).Should().Be(IndicesNuevos.Length, "los nueve indexan la expresión que emite la consulta");

        (await IndicesExistentesAsync(IndicesRetirados)).Should().BeEmpty();
        (await IndicesExistentesAsync(IndicesConservados)).Should().BeEquivalentTo(IndicesConservados);
    }

    /// <summary>
    /// <c>unaccent</c> va antes que <c>upper</c> («ß» pasa a «SS»), y el patrón escapa los tres
    /// caracteres con significado en <c>LIKE … ESCAPE '\'</c> después de normalizar.
    /// El último caso es texto descompuesto (NFD), como el que llega pegado desde macOS o desde
    /// algunos PDF: «n» seguida de la virgulilla combinante U+0303, no la «ñ» de un solo carácter.
    /// </summary>
    [Theory]
    [InlineData("García Núñez", "GARCIA NUNEZ")]
    [InlineData("PINGÜINO çedilla", "PINGUINO CEDILLA")]
    [InlineData("Weiß", "WEISS")]
    [InlineData("Nuñez", "NUNEZ")]
    public async Task La_funcion_de_texto_quita_acentos_y_pasa_a_mayusculas(string texto, string esperado)
    {
        (await EscalarAsync<string>("SELECT public.texto_de_busqueda(@texto)", ("texto", texto))).Should().Be(esperado);
    }

    [Theory]
    [InlineData("garcía", "%GARCIA%")]
    [InlineData("100%", @"%100\%%")]
    [InlineData("a_b", @"%A\_B%")]
    [InlineData(@"a\b", @"%A\\B%")]
    public async Task La_funcion_de_patron_normaliza_escapa_y_envuelve(string termino, string esperado)
    {
        (await EscalarAsync<string>("SELECT public.patron_de_busqueda(@termino)", ("termino", termino))).Should().Be(esperado);
    }

    /// <summary>Los tres roles con los que la aplicación lee pueden ejecutar las dos funciones.</summary>
    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task Los_roles_de_la_aplicacion_pueden_ejecutar_las_funciones(string rol)
    {
        (await EscalarAsync<bool>(
            """
            SELECT has_function_privilege(@rol, 'public.texto_de_busqueda(text)', 'EXECUTE')
               AND has_function_privilege(@rol, 'public.patron_de_busqueda(text)', 'EXECUTE')
            """,
            ("rol", rol))).Should().BeTrue();
    }

    /// <summary>
    /// La consulta que emite EF compara la misma expresión que indexa la migración, con
    /// <c>LIKE</c> (lo único a lo que sirve un índice trigram, no <c>strpos</c>), y el
    /// planificador puede resolverla con el índice. Se mide como propietario de la base, sin RLS:
    /// lo que se fija es que la expresión indexada y la consultada son la misma. Bajo RLS, como
    /// <c>cae_app_runtime</c>, el planificador no usa un índice trigram para un <c>LIKE</c> (no es
    /// <c>LEAKPROOF</c>), ni estos ni los anteriores sobre <c>upper(columna)</c>.
    /// </summary>
    [Fact]
    public async Task La_consulta_de_EF_compara_la_expresion_indexada_y_el_indice_le_sirve()
    {
        // Con un Tenant en el contexto: sin él, el filtro global de EF deja la consulta en «WHERE FALSE».
        await using var contexto = NuevoContexto(new TenantActualAmbiental { TenantId = Guid.NewGuid() });
        var termino = "garcia";

        var sql = contexto.Trabajadores.Where(t => TextoDeBusqueda.Contiene(t.Nombre, termino)).ToQueryString();

        sql.Should().Contain("""public.texto_de_busqueda(t."Nombre") LIKE public.patron_de_busqueda(@""")
            .And.Contain(@"ESCAPE '\'")
            .And.NotContain("strpos");

        var plan = await PlanSinRecorridoSecuencialAsync(
            """
            EXPLAIN (COSTS OFF)
            SELECT t."Id" FROM public."Trabajadores" AS t
            WHERE public.texto_de_busqueda(t."Nombre") LIKE public.patron_de_busqueda('garcia') ESCAPE '\'
            """);

        plan.Should().Contain("IX_Trabajadores_Nombre_Busqueda");
    }

    [Fact]
    public async Task Deshacer_la_migracion_retira_lo_nuevo_y_devuelve_los_indices_retirados()
    {
        await using var contexto = NuevoContexto();
        var migrador = contexto.GetService<IMigrator>();

        await migrador.MigrateAsync(MigracionAnterior(contexto));

        (await EscalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'unaccent'")).Should().Be(0);
        (await EscalarAsync<long>(
            "SELECT count(*) FROM pg_proc WHERE pronamespace = 'public'::regnamespace AND proname IN ('texto_de_busqueda', 'patron_de_busqueda')"))
            .Should().Be(0);
        (await IndicesExistentesAsync(IndicesNuevos)).Should().BeEmpty();
        (await IndicesExistentesAsync(IndicesRetirados)).Should().BeEquivalentTo(IndicesRetirados);
        (await IndicesExistentesAsync(IndicesConservados)).Should().BeEquivalentTo(IndicesConservados);
        (await EscalarAsync<long>(
            "SELECT count(*) FROM pg_indexes WHERE schemaname = 'public' AND indexname = ANY(@nombres) AND indexdef LIKE '%(upper(%gin_trgm_ops%'",
            ("nombres", IndicesRetirados))).Should().Be(IndicesRetirados.Length, "vuelven con la expresión que tenían");

        // Y vuelve a aplicarse sobre lo que dejó el Down.
        await migrador.MigrateAsync();

        (await IndicesExistentesAsync(IndicesNuevos)).Should().BeEquivalentTo(IndicesNuevos);
        (await IndicesExistentesAsync(IndicesRetirados)).Should().BeEmpty();
    }

    /// <summary>La migración inmediatamente anterior a la del cambio: así no caduca cuando entre otra posterior.</summary>
    private static string MigracionAnterior(CaeManagerDbContext contexto)
    {
        var migraciones = contexto.Database.GetMigrations().ToList();
        var indice = migraciones.IndexOf(MigracionDelCambio);
        indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
        return migraciones[indice - 1];
    }

    private async Task<string[]> IndicesExistentesAsync(string[] nombres)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT indexname::text FROM pg_indexes WHERE schemaname = 'public' AND indexname = ANY(@nombres)", conexion);
        comando.Parameters.AddWithValue("nombres", nombres);

        var existentes = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            existentes.Add(lector.GetString(0));

        return [.. existentes];
    }

    /// <summary>
    /// El plan con el recorrido secuencial desaconsejado: en una tabla vacía el planificador lo
    /// elegiría siempre, y lo que se pregunta es si el índice es aplicable a la consulta.
    /// </summary>
    private async Task<string> PlanSinRecorridoSecuencialAsync(string explain)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var ajuste = new NpgsqlCommand("SET LOCAL enable_seqscan = off", conexion, transaccion))
            await ajuste.ExecuteNonQueryAsync();

        var lineas = new List<string>();
        await using (var comando = new NpgsqlCommand(explain, conexion, transaccion))
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                lineas.Add(lector.GetString(0));
        }

        return string.Join('\n', lineas);
    }

    private async Task<T> EscalarAsync<T>(string sql, params (string Nombre, object Valor)[] parametros)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        foreach (var (nombre, valor) in parametros)
            comando.Parameters.AddWithValue(nombre, valor);
        var resultado = await comando.ExecuteScalarAsync();
        return resultado is DBNull or null ? default! : (T)resultado;
    }

    private CaeManagerDbContext NuevoContexto(TenantActualAmbiental? tenant = null)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant ?? new TenantActualAmbiental());
    }
}
