using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// El procedimiento con el que se construye la plantilla migrada
/// (<see cref="PlantillaMigradaDePruebas"/>): una sola construcción aunque la
/// pidan varios a la vez, publicación atómica, y nunca una plantilla a medias.
///
/// <para>
/// Cada test usa una plantilla <b>privada</b> (discriminador propio) y una
/// «migración» de mentira que solo deja una tabla marcadora: aquí se prueba el
/// procedimiento, no el contenido. La plantilla de la suite, que las demás clases
/// están clonando en paralelo, no se toca; que su contenido equivale a una base
/// migrada de verdad lo exige <c>EquivalenciaDelClonConLaBaseMigradaTests</c>.
/// </para>
///
/// <para>
/// Los tres tests del material del resumen no construyen nada: exigen que el
/// nombre de la plantilla dependa de las migraciones y de los roles <b>reales</b>,
/// que es lo que impide clonar en local una plantilla caducada.
/// </para>
/// </summary>
public class PlantillaMigradaDePruebasTests : IAsyncLifetime
{
    private const string TablaMarcadora = "marca_de_plantilla_completa";

    private readonly string _discriminador = $"t{Guid.NewGuid():N}"[..9] + "_";
    private readonly List<string> _basesCreadas = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var conexion = await AbrirMantenimientoAsync();
        var sobrantes = await ListarAsync(conexion, $"%{_discriminador}%");
        foreach (var sobrante in sobrantes.Concat(_basesCreadas).Distinct())
            await EjecutarAsync(conexion, $"DROP DATABASE IF EXISTS \"{sobrante}\" WITH (FORCE);");
    }

    [Fact]
    public async Task Dos_constructores_a_la_vez_migran_la_plantilla_una_sola_vez()
    {
        // Dos instancias no comparten el cerrojo del proceso: lo único que las
        // ordena es el cerrojo consultivo de PostgreSQL, como a dos procesos.
        var primera = new PlantillaMigradaDePruebas(_discriminador, MigrarDeMentiraAsync);
        var segunda = new PlantillaMigradaDePruebas(_discriminador, MigrarDeMentiraAsync);

        var nombres = await Task.WhenAll(
            Task.Run(() => AsegurarConConexionPropia(primera)),
            Task.Run(() => AsegurarConConexionPropia(segunda)));

        (primera.PlantillasConstruidas + segunda.PlantillasConstruidas).Should().Be(1,
            "la segunda en llegar encuentra la plantilla ya publicada y no vuelve a migrarla");
        nombres[0].Should().Be(nombres[1]);

        await using var conexion = await AbrirMantenimientoAsync();
        (await ListarAsync(conexion, $"%{_discriminador}%")).Should().Equal([nombres[0]],
            "queda la plantilla y ninguna provisional");
        (await AdmiteConexionesAsync(conexion, nombres[0])).Should().BeFalse(
            "una plantilla cerrada a conexiones no puede modificarse ni bloquear el clonado");
    }

    [Fact]
    public async Task Una_provisional_abandonada_no_se_usa_nunca_y_la_sustituye_una_construccion_completa()
    {
        var plantilla = new PlantillaMigradaDePruebas(_discriminador, MigrarDeMentiraAsync);
        await using var conexion = await AbrirMantenimientoAsync();

        // Lo que deja un proceso que murió migrando: la provisional, sin la marca.
        var esperado = plantilla.NombreVigente(conexion);
        var abandonada = PlantillaMigradaDePruebas.NombreProvisionalDe(esperado);
        await EjecutarAsync(conexion, $"CREATE DATABASE \"{abandonada}\";");

        var publicada = plantilla.Asegurar(conexion);

        publicada.Should().Be(esperado);
        (await ListarAsync(conexion, $"%{_discriminador}%")).Should().Equal([publicada],
            "la provisional abandonada se borra antes de construir y no queda ninguna al terminar");

        var clon = await ClonarAsync(plantilla);
        (await TieneLaMarcaAsync(clon)).Should().BeTrue(
            "el clon sale de la plantilla construida entera, no de la provisional vacía que dejó el proceso muerto");
    }

    [Fact]
    public async Task Si_la_construccion_falla_a_mitad_no_queda_ninguna_plantilla_que_clonar()
    {
        var rota = new PlantillaMigradaDePruebas(_discriminador, async cadena =>
        {
            await CrearBaseAsync(cadena);
            throw new InvalidOperationException("la migración se cae a mitad");
        });
        await using var conexion = await AbrirMantenimientoAsync();

        var construir = () => rota.Asegurar(conexion);

        construir.Should().Throw<InvalidOperationException>().WithMessage("*a mitad*");
        (await ListarAsync(conexion, $"{PlantillaMigradaDePruebas.Prefijo}{_discriminador}%")).Should().BeEmpty(
            "el nombre definitivo solo aparece con el renombrado final, que no llegó a ocurrir");

        // Y el siguiente no hereda nada de la rota.
        var sana = new PlantillaMigradaDePruebas(_discriminador, MigrarDeMentiraAsync);
        var clon = await ClonarAsync(sana);
        (await TieneLaMarcaAsync(clon)).Should().BeTrue();
        sana.PlantillasConstruidas.Should().Be(1);
    }

    [Fact]
    public async Task Si_alguien_borra_la_plantilla_el_siguiente_clon_la_reconstruye()
    {
        var plantilla = new PlantillaMigradaDePruebas(_discriminador, MigrarDeMentiraAsync);
        await ClonarAsync(plantilla);

        await using var conexion = await AbrirMantenimientoAsync();
        var nombre = plantilla.Asegurar(conexion);
        await EjecutarAsync(conexion, $"DROP DATABASE \"{nombre}\" WITH (FORCE);");

        var clon = await ClonarAsync(plantilla);

        (await TieneLaMarcaAsync(clon)).Should().BeTrue();
        plantilla.PlantillasConstruidas.Should().Be(2);
    }

    [Fact]
    public async Task Al_construir_se_retiran_las_plantillas_de_meses_anteriores_y_se_respetan_las_del_mes_en_curso()
    {
        await using var conexion = await AbrirMantenimientoAsync();
        var mes = await MesDelServidorAsync(conexion);
        var deOtroMes = PlantillaMigradaDePruebas.NombreDePlantilla("otrocontenido", "200001", _discriminador);
        var deEsteMes = PlantillaMigradaDePruebas.NombreDePlantilla("otrocontenido", mes, _discriminador);
        await EjecutarAsync(conexion, $"CREATE DATABASE \"{deOtroMes}\";");
        await EjecutarAsync(conexion, $"CREATE DATABASE \"{deEsteMes}\";");

        var plantilla = new PlantillaMigradaDePruebas(_discriminador, MigrarDeMentiraAsync);
        var publicada = plantilla.Asegurar(conexion);

        (await ListarAsync(conexion, $"%{_discriminador}%")).Should().BeEquivalentTo([publicada, deEsteMes],
            "la de un mes pasado ya no la puede pedir nadie; la de este mes con otro contenido puede ser de otra rama");
    }

    [Fact]
    public void El_nombre_de_la_plantilla_cambia_con_las_migraciones_con_los_roles_con_el_proveedor_y_con_el_mes()
    {
        var resumen = PlantillaMigradaDePruebas.Resumir("CREATE TABLE a();", "ef=10", "CREATE ROLE r;");

        PlantillaMigradaDePruebas.Resumir("CREATE TABLE a();", "ef=10", "CREATE ROLE r;").Should().Be(resumen);
        PlantillaMigradaDePruebas.Resumir("CREATE TABLE a(); -- sin FORCE", "ef=10", "CREATE ROLE r;").Should().NotBe(resumen);
        PlantillaMigradaDePruebas.Resumir("CREATE TABLE a();", "ef=11", "CREATE ROLE r;").Should().NotBe(resumen);
        PlantillaMigradaDePruebas.Resumir("CREATE TABLE a();", "ef=10", "CREATE ROLE s;").Should().NotBe(resumen);

        var octubre = PlantillaMigradaDePruebas.NombreDePlantilla(resumen, "202610");
        var noviembre = PlantillaMigradaDePruebas.NombreDePlantilla(resumen, "202611");
        octubre.Should().NotBe(noviembre,
            "la línea base crea las particiones mensuales de la auditoría a partir de now()");
        octubre.Length.Should().BeLessThanOrEqualTo(63, "PostgreSQL trunca los identificadores más largos");

        PlantillaMigradaDePruebas.EsDeUnMesAnterior(octubre, "202611").Should().BeTrue();
        PlantillaMigradaDePruebas.EsDeUnMesAnterior(octubre, "202610").Should().BeFalse();
        PlantillaMigradaDePruebas.EsDeUnMesAnterior(noviembre, "202610").Should().BeFalse();
        PlantillaMigradaDePruebas.EsDeUnMesAnterior("caemanager_plantilla_sin_mes", "202610").Should().BeFalse(
            "lo que no se reconoce no se borra");
    }

    [Fact]
    public async Task Un_fallo_al_construir_se_recuerda_y_no_se_vuelve_a_migrar_en_el_mismo_proceso()
    {
        var migraciones = 0;
        var causa = new TimeoutException("la causa original");
        var rota = new PlantillaMigradaDePruebas(_discriminador, _ =>
        {
            Interlocked.Increment(ref migraciones);
            throw new InvalidOperationException("la migración se cae a mitad", causa);
        });
        await using var conexion = await AbrirMantenimientoAsync();

        var construir = () => rota.Asegurar(conexion);
        var primero = construir.Should().Throw<InvalidOperationException>().Which;
        var segundo = construir.Should().Throw<InvalidOperationException>().Which;

        migraciones.Should().Be(1,
            "con una migración rota, cada test que pida su base recibe el fallo sin volver a tomar el cerrojo ni a migrar");
        segundo.Should().BeSameAs(primero, "es el fallo de la primera construcción, no uno nuevo");
        segundo.Message.Should().Be("la migración se cae a mitad");
        segundo.InnerException.Should().BeSameAs(causa);

        // Por Clonar, que es por donde entra cada test de la suite.
        var clonar = () => rota.Clonar(BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia());
        clonar.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(primero);
        migraciones.Should().Be(1);
    }

    [Fact]
    public void El_guion_que_se_resume_es_el_de_todas_las_migraciones_del_ensamblado()
    {
        var guion = PlantillaMigradaDePruebas.LeerMaterialDelResumen().GuionDeMigraciones;

        // Del propio ensamblado, no de una lista escrita aquí: una migración nueva
        // entra sola en la comprobación.
        var identificadores = typeof(CaeManager.Migrations.PostgreSQL.Migrations.LineaBaseCompactada).Assembly
            .GetTypes()
            .Where(tipo => tipo.IsSubclassOf(typeof(Migration)) && !tipo.IsAbstract)
            .Select(tipo => tipo.GetCustomAttribute<MigrationAttribute>()?.Id ?? $"(sin [Migration]: {tipo.Name})")
            .ToList();
        identificadores.Should().NotBeEmpty("el ensamblado de migraciones tiene migraciones");

        guion.Should().NotBeEmpty(
            "con un guion vacío el nombre de la plantilla no cambiaría al cambiar una migración "
            + "y se clonaría una plantilla caducada sin que nada fallase");
        identificadores.Where(id => !guion.Contains(id, StringComparison.Ordinal)).Should().BeEmpty(
            "el guion que decide el nombre de la plantilla tiene que incluir todas las migraciones del ensamblado");
        guion.Contains("FORCE ROW LEVEL SECURITY", StringComparison.Ordinal).Should().BeTrue(
            "el guion lleva el SQL de las migraciones, no solo sus identificadores: "
            + "quitar un FORCE de una migración tiene que cambiar el nombre");
    }

    [Fact]
    public void El_guion_de_roles_que_se_resume_es_el_del_bootstrap_de_cluster()
    {
        var roles = PlantillaMigradaDePruebas.LeerMaterialDelResumen().GuionDeRoles;

        var delRepositorio = File.ReadAllText(
            Path.Combine(RaizDelRepositorio(), "deploy", "bootstrap", "roles-de-cluster.sql"));

        string.Equals(roles, delRepositorio, StringComparison.Ordinal).Should().BeTrue(
            "lo que entra en el nombre es deploy/bootstrap/roles-de-cluster.sql tal como está en el árbol");
        roles.Contains("cae_app_runtime", StringComparison.Ordinal).Should().BeTrue(
            "es el guion que crea el rol con el que corre la aplicación bajo RLS");
    }

    [Fact]
    public async Task El_nombre_vigente_cambia_con_un_solo_caracter_de_las_migraciones_o_de_los_roles_reales()
    {
        var material = PlantillaMigradaDePruebas.LeerMaterialDelResumen();
        var vigente = PlantillaMigradaDePruebas.ResumenVigente;

        PlantillaMigradaDePruebas
            .Resumir(material.GuionDeMigraciones, material.VersionesDelProveedor, material.GuionDeRoles)
            .Should().Be(vigente, "el resumen vigente sale de ese material, y generarlo otra vez da lo mismo");
        PlantillaMigradaDePruebas
            .Resumir(ConUnCaracterCambiado(material.GuionDeMigraciones), material.VersionesDelProveedor, material.GuionDeRoles)
            .Should().NotBe(vigente, "tocar una migración tiene que dar otra plantilla");
        PlantillaMigradaDePruebas
            .Resumir(material.GuionDeMigraciones, material.VersionesDelProveedor, ConUnCaracterCambiado(material.GuionDeRoles))
            .Should().NotBe(vigente, "tocar los roles de clúster tiene que dar otra plantilla");

        await using var conexion = await AbrirMantenimientoAsync();
        PlantillaMigradaDePruebas.DeLaSuite.NombreVigente(conexion).Should().StartWith(
            $"{PlantillaMigradaDePruebas.Prefijo}{vigente}_",
            "ese resumen es el que lleva el nombre de la plantilla que clona la suite");
    }

    // ---- Apoyo ----

    private static string ConUnCaracterCambiado(string texto)
    {
        texto.Should().NotBeEmpty("no se puede alterar un carácter de un material vacío");
        var posicion = texto.Length / 2;
        var otro = texto[posicion] == 'x' ? 'y' : 'x';
        return string.Concat(texto.AsSpan(0, posicion), otro.ToString(), texto.AsSpan(posicion + 1));
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);
        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName
            ?? throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);
    }

    private static string AsegurarConConexionPropia(PlantillaMigradaDePruebas plantilla)
    {
        using var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        conexion.Open();
        return plantilla.Asegurar(conexion);
    }

    private async Task<string> ClonarAsync(PlantillaMigradaDePruebas plantilla)
    {
        var cadena = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();
        _basesCreadas.Add(new NpgsqlConnectionStringBuilder(cadena).Database!);
        await Task.Run(() => plantilla.Clonar(cadena));
        return cadena;
    }

    private static async Task MigrarDeMentiraAsync(string cadena)
    {
        await CrearBaseAsync(cadena);
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await EjecutarAsync(conexion, $"CREATE TABLE {TablaMarcadora} (id integer PRIMARY KEY);");
    }

    private static async Task CrearBaseAsync(string cadena)
    {
        var nombre = new NpgsqlConnectionStringBuilder(cadena).Database;
        await using var conexion = await AbrirMantenimientoAsync();
        await EjecutarAsync(conexion, $"CREATE DATABASE \"{nombre}\";");
    }

    private static async Task<bool> TieneLaMarcaAsync(string cadena)
    {
        var sinPool = new NpgsqlConnectionStringBuilder(cadena) { Pooling = false }.ConnectionString;
        await using var conexion = new NpgsqlConnection(sinPool);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand($"SELECT to_regclass('public.{TablaMarcadora}') IS NOT NULL;", conexion);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<NpgsqlConnection> AbrirMantenimientoAsync()
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaDeMantenimientoSinPool());
        await conexion.OpenAsync();
        return conexion;
    }

    private static async Task<string> MesDelServidorAsync(NpgsqlConnection conexion)
    {
        await using var comando = new NpgsqlCommand("SELECT to_char(now() AT TIME ZONE 'UTC', 'YYYYMM');", conexion);
        return (string)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<bool> AdmiteConexionesAsync(NpgsqlConnection conexion, string baseDeDatos)
    {
        await using var comando = new NpgsqlCommand("SELECT datallowconn FROM pg_database WHERE datname = @nombre;", conexion);
        comando.Parameters.AddWithValue("nombre", baseDeDatos);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private static async Task<List<string>> ListarAsync(NpgsqlConnection conexion, string patron)
    {
        var nombres = new List<string>();
        await using var comando = new NpgsqlCommand(
            "SELECT datname FROM pg_database WHERE datname LIKE @patron ORDER BY datname;", conexion);
        comando.Parameters.AddWithValue("patron", patron.Replace("_", "\\_", StringComparison.Ordinal));
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            nombres.Add(lector.GetString(0));
        return nombres;
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = new NpgsqlCommand(sql, conexion) { CommandTimeout = 120 };
        await comando.ExecuteNonQueryAsync();
    }
}
