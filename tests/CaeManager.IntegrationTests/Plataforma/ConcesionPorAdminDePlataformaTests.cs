using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// PD-A3, migración <c>RlsConcesionPorAdminDePlataforma</c>: la segunda rama del
/// <c>WITH CHECK</c> de <c>ConcesionesPrivilegio</c>/<c>TenantsAlcanzadosPorConcesion</c>,
/// que por primera vez admite conceder un privilegio a un TERCERO. Probado, como
/// <see cref="RlsPlanoPrivilegioTests"/>, contra el rol restringido
/// (<c>cae_app_runtime</c>) — conectar como superusuario no ejercitaría la política.
///
/// La propiedad central: un AdminPlataforma solo puede acuñar
/// <see cref="CapacidadPrivilegio.Aprovisionamiento"/>, nunca global, y solo sobre
/// tenants que su propia concesión ya cubre — la comprobación de cobertura vive en
/// la fila HIJA (<c>TenantsAlcanzadosPorConcesion</c>), porque es la única que lleva
/// <c>TenantId</c>; el padre es deliberadamente más laxo (solo exige AdminPlataforma
/// vigente en ALGÚN tenant) y depende de la hija para la prueba fuerte.
/// </summary>
public class ConcesionPorAdminDePlataformaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _admin = Guid.NewGuid();
    private readonly Guid _adminGlobal = Guid.NewGuid();
    private readonly Guid _sinAutoridad = Guid.NewGuid();
    private readonly Guid _beneficiario = Guid.NewGuid();
    private readonly Guid _tenantCubierto = Guid.NewGuid();
    private readonly Guid _tenantNoCubierto = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;

        // _admin: AdminPlataforma vigente, acotado a _tenantCubierto.
        var concesionAdmin = ConcesionPrivilegio.SobreTenants(
            _admin, CapacidadPrivilegio.AdminPlataforma, [_tenantCubierto],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddDays(30));

        // _adminGlobal: AdminPlataforma global.
        var concesionAdminGlobal = ConcesionPrivilegio.Global(
            _adminGlobal, vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddDays(30));

        contexto.ConcesionesPrivilegio.AddRange(concesionAdmin, concesionAdminGlobal);
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino positivo ─────────────────────────────────────────────────────

    [Fact]
    public async Task Un_admin_con_AdminPlataforma_sobre_el_tenant_concede_Aprovisionamiento_a_un_tercero()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var concesionId = await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: false, concedidaPor: _admin);

        (await InsertarAlcanceAsync(conexion, concesionId, _tenantCubierto)).Should().Be(1,
            "el tenant está cubierto por la concesión del propio admin");
    }

    [Fact]
    public async Task Un_admin_con_concesion_global_tambien_puede_conceder()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_adminGlobal);

        var concesionId = await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: false, concedidaPor: _adminGlobal);

        (await InsertarAlcanceAsync(conexion, concesionId, _tenantNoCubierto)).Should().Be(1,
            "una concesión global cubre cualquier tenant, incluido uno que no aparece en ninguna fila de alcance");
    }

    // ── El padre es laxo, la hija es la prueba fuerte ──────────────────────

    [Fact]
    public async Task El_padre_se_inserta_aunque_el_tenant_no_este_cubierto_pero_la_hija_lo_rechaza()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        // El padre no lleva tenant: solo exige AdminPlataforma vigente en ALGUNO.
        var concesionId = await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: false, concedidaPor: _admin);

        // La hija SÍ comprueba cobertura, y _tenantNoCubierto no está entre los
        // tenants de la concesión de _admin.
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""TenantsAlcanzadosPorConcesion"" (""Id"", ""ConcesionPrivilegioId"", ""TenantId"")
VALUES (gen_random_uuid(), @concesion, @tenant);";
        comando.Parameters.AddWithValue("concesion", concesionId);
        comando.Parameters.AddWithValue("tenant", _tenantNoCubierto);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "sin esta barrera, un admin acotado a un tenant podría conceder aprovisionamiento sobre " +
                "cualquier otro con solo mentir en la fila de alcance");
    }

    // ── Restricciones del padre, ninguna opcional ──────────────────────────

    [Fact]
    public async Task No_se_puede_conceder_alcance_global_por_esta_via()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: true, concedidaPor: _admin);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "EsAlcanceGlobal=true saltaría por completo la comprobación de cobertura de la hija");
    }

    [Fact]
    public async Task No_se_puede_conceder_otra_capacidad_que_no_sea_Aprovisionamiento()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarConcesionAsync(
            conexion, _beneficiario, "AdminPlataforma", esAlcanceGlobal: false, concedidaPor: _admin);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "un admin no puede acuñar más AdminPlataforma ni BreakGlass por esta vía, solo Aprovisionamiento");
    }

    [Fact]
    public async Task No_se_puede_declarar_concedida_por_otro_usuario_distinto_de_quien_conecta()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_admin);

        var accion = async () => await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: false, concedidaPor: _adminGlobal);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "ConcedidaPorUsuarioId tiene que coincidir con quien realmente ejecuta el INSERT");
    }

    [Fact]
    public async Task Un_usuario_sin_ninguna_concesion_de_AdminPlataforma_no_puede_conceder_nada()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_sinAutoridad);

        var accion = async () => await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: false, concedidaPor: _sinAutoridad);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ── La función SECURITY DEFINER resiste suplantación por tabla temporal ─

    [Fact]
    public async Task La_funcion_no_se_deja_enganar_por_una_tabla_temporal_con_el_mismo_nombre()
    {
        // Si search_path incluyera 'public' sin fijar pg_temp al final (o lo
        // omitiera), una tabla temporal "ConcesionesPrivilegio" creada por
        // CUALQUIER sesión con privilegio de crear temporales (PUBLIC lo tiene
        // por defecto) podría interponerse delante de la real dentro de la
        // función SECURITY DEFINER. Aquí se demuestra que no: la función sigue
        // consultando la tabla real (cualificada public.) pase lo que pase.
        await using var conexion = await AbrirRestringidaComoAsync(_sinAutoridad);

        await using (var crearFalsa = conexion.CreateCommand())
        {
            crearFalsa.CommandText = @"
CREATE TEMP TABLE ""ConcesionesPrivilegio"" (
    ""UsuarioPlataformaId"" uuid, ""Capacidad"" text, ""Estado"" text,
    ""VigenciaDesde"" timestamp, ""VigenciaHasta"" timestamp, ""EsAlcanceGlobal"" boolean);
INSERT INTO ""ConcesionesPrivilegio""
VALUES (@usuario, 'AdminPlataforma', 'Vigente', now() - interval '1 minute', NULL, true);";
            crearFalsa.Parameters.AddWithValue("usuario", _sinAutoridad);
            await crearFalsa.ExecuteNonQueryAsync();
        }

        // Con la tabla falsa "vigente" en su propio search_path de sesión, un
        // SELECT normal del propio usuario la vería — control positivo de que
        // la suplantación local funciona y no es un test vacío.
        await using (var controlPositivo = conexion.CreateCommand())
        {
            controlPositivo.CommandText = "SELECT count(*) FROM \"ConcesionesPrivilegio\" WHERE \"UsuarioPlataformaId\" = @usuario;";
            controlPositivo.Parameters.AddWithValue("usuario", _sinAutoridad);
            (await controlPositivo.ExecuteScalarAsync()).Should().Be(1L,
                "control positivo: la tabla temporal existe y el propio usuario la ve en su search_path normal");
        }

        // Pero la función SECURITY DEFINER, con su search_path propio
        // (pg_catalog, pg_temp — sin 'public', y la relación cualificada
        // public. en el cuerpo), tiene que seguir ignorando la falsa.
        var accion = async () => await InsertarConcesionAsync(
            conexion, _beneficiario, "Aprovisionamiento", esAlcanceGlobal: false, concedidaPor: _sinAutoridad);

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                "si esto pasara en verde (INSERT permitido), la función SECURITY DEFINER habría leído la " +
                "tabla temporal falsa en vez de la real, y _sinAutoridad se habría autoconcedido autoridad");
    }

    // ── Catálogo: EXECUTE de las funciones nuevas ───────────────────────────

    [Theory]
    [InlineData("cae_app_runtime", true)]
    [InlineData("cae_app_soporte", false)]
    [InlineData("cae_app_aprovisionamiento", false)]
    public async Task Solo_cae_app_runtime_puede_ejecutar_las_funciones_de_autoridad(string rol, bool esperado)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_function_privilege(@rol, 'app_es_admin_plataforma(uuid)', 'EXECUTE');";
        comando.Parameters.AddWithValue("rol", rol);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().Be(esperado,
            "conceder EXECUTE a cae_app_aprovisionamiento dejaría que una sesión de aprovisionamiento " +
            "acuñara privilegios de plataforma para sí misma, que es justo lo que esta capacidad no debe poder hacer");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> AbrirRestringidaComoAsync(Guid usuarioId)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using (var fijar = conexion.CreateCommand())
        {
            fijar.CommandText = "SELECT set_config('app.usuario_id', @valor, false);";
            fijar.Parameters.AddWithValue("valor", usuarioId.ToString());
            await fijar.ExecuteNonQueryAsync();
        }

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task<Guid> InsertarConcesionAsync(
        NpgsqlConnection conexion, Guid beneficiario, string capacidad, bool esAlcanceGlobal, Guid concedidaPor)
    {
        var id = Guid.NewGuid();

        await using var comando = conexion.CreateCommand();
        // Cualificado con public.: sin esto, en el test de suplantación por
        // tabla temporal (que crea "ConcesionesPrivilegio" en pg_temp sobre la
        // MISMA conexión) este INSERT sin cualificar resolvería contra la
        // temporal, no la real — pg_temp se busca antes que el resto del
        // search_path para nombres sin esquema, sin necesidad de aparecer en
        // él. Cualificar aquí dirige el INSERT a la tabla real a propósito,
        // que es lo que el test necesita ejercitar (que la FUNCIÓN interna,
        // no este INSERT, ignore la tabla falsa).
        comando.CommandText = @"
INSERT INTO public.""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"", ""VigenciaDesde"",
     ""Estado"", ""ConcedidaPorUsuarioId"", ""MotivoConcesion"", ""CreadoEnUtc"", ""Version"")
VALUES (@id, @beneficiario, @capacidad, @global, now(), 'Vigente', @concedidaPor,
        'Aprovisionamiento inicial (test)', now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("id", id);
        comando.Parameters.AddWithValue("beneficiario", beneficiario);
        comando.Parameters.AddWithValue("capacidad", capacidad);
        comando.Parameters.AddWithValue("global", esAlcanceGlobal);
        comando.Parameters.AddWithValue("concedidaPor", concedidaPor);
        await comando.ExecuteNonQueryAsync();

        return id;
    }

    private static async Task<int> InsertarAlcanceAsync(NpgsqlConnection conexion, Guid concesionId, Guid tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""TenantsAlcanzadosPorConcesion"" (""Id"", ""ConcesionPrivilegioId"", ""TenantId"")
VALUES (gen_random_uuid(), @concesion, @tenant);";
        comando.Parameters.AddWithValue("concesion", concesionId);
        comando.Parameters.AddWithValue("tenant", tenantId);
        return await comando.ExecuteNonQueryAsync();
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantCubierto };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
