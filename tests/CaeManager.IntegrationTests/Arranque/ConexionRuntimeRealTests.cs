using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>La vía de prueba que reproduce producción</b>: conectar autenticando como
/// <c>cae_app_runtime</c>, no adoptarlo con <c>SET ROLE</c> desde el propietario.
///
/// <para>
/// La diferencia no es cosmética. <c>SET ROLE</c> demuestra que las políticas se
/// aplican, pero parte de una sesión que <b>ya entró como superusuario</b>; una
/// conexión de login reproduce además la autenticación y los privilegios
/// efectivos del rol. Todo lo que se construya encima —la prueba de arranque
/// completa— descansa en que esta vía funcione, así que se comprueba aparte y
/// antes.
/// </para>
///
/// <para>
/// <b>Solo es posible desde #256.</b> El bootstrap de clúster convergía
/// <c>cae_app_runtime</c> a <c>NOLOGIN</c> en cada ejecución, así que el arnés le
/// habría retirado el LOGIN inmediatamente después de concedérselo.
/// </para>
/// </summary>
public class ConexionRuntimeRealTests : IAsyncLifetime
{
    private readonly string _cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly List<Guid> _tenants = [];

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContextoPropietario();
        await contexto.Database.MigrateAsync();

        for (var i = 0; i < 3; i++)
        {
            var tenant = new Tenant($"Tenant de prueba {i}");
            contexto.Tenants.Add(tenant);
            _tenants.Add(tenant.Id);
        }

        await contexto.SaveChangesAsync();

        // Deja una operación raíz por tenant: filas reales, con propietarios
        // distintos, que es lo que hace discriminante la prueba de exclusión.
        await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaPropietario);

    /// <summary>Propiedad 1 — identidad: autentica de verdad y sin privilegios.</summary>
    [Fact]
    public async Task La_conexion_autentica_como_cae_app_runtime_y_no_puede_saltarse_RLS()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT session_user,
       current_user,
       current_setting('is_superuser'),
       (SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user);";

        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetString(0).Should().Be("cae_app_runtime",
            "es una conexión de LOGIN, no un SET ROLE sobre una sesión de superusuario");
        lector.GetString(1).Should().Be("cae_app_runtime");
        lector.GetString(2).Should().Be("off", "un superusuario ignoraría RLS con FORCE o sin él");
        lector.GetBoolean(3).Should().BeFalse("BYPASSRLS haría inútiles todas las políticas");
    }

    /// <summary>
    /// Propiedades 2, 3 y 4 — ámbito, exclusión y resultado, con control negativo.
    ///
    /// <para>
    /// Los tres recuentos son distintos entre sí a propósito: si "ve lo suyo" y
    /// "lo ve todo" dieran el mismo número, el test no distinguiría enforcement
    /// de ausencia de enforcement.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Esa_conexion_ve_solo_las_filas_del_tenant_fijado_en_la_sesion()
    {
        var total = await ContarRaicesComoPropietarioAsync();
        total.Should().BeGreaterThan(1,
            "hacen falta varios tenants para que 've lo suyo' y 'lo ve todo' sean números distintos");

        await using var conexion = await AbrirComoRuntimeAsync();

        (await ContarRaicesAsync(conexion)).Should().Be(0,
            "sin app.tenant_id la política no puede emparejar nada: la ausencia de coordenada no abre, cierra");

        await FijarTenantAsync(conexion, _tenants[0]);

        (await ContarRaicesAsync(conexion)).Should().Be(1,
            "con la coordenada puesta ve exactamente la raíz de SU tenant");

        (await PropietarioDeLaUnicaRaizAsync(conexion)).Should().Be(_tenants[0],
            "y es la suya, no la de otro: un recuento correcto con la fila equivocada seguiría siendo un fallo");

        (await ContarRaicesComoPropietarioAsync()).Should().Be(total,
            "control negativo: el propietario sigue viéndolas todas, así que el 1 de arriba es RLS " +
            "filtrando y no filas que hayan desaparecido");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario));
        await conexion.OpenAsync();
        return conexion;
    }

    private static async Task FijarTenantAsync(NpgsqlConnection conexion, Guid tenant)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @tenant, false);";
        comando.Parameters.AddWithValue("tenant", tenant.ToString());
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<int> ContarRaicesAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""AsignacionesOperacion"" WHERE ""EsRaiz"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    private static async Task<Guid> PropietarioDeLaUnicaRaizAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""PropietarioTenantId"" FROM ""AsignacionesOperacion"" WHERE ""EsRaiz"";";
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    private async Task<int> ContarRaicesComoPropietarioAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaPropietario);
        await conexion.OpenAsync();
        return await ContarRaicesAsync(conexion);
    }

    private CaeManagerDbContext CrearContextoPropietario()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(
            opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }
}
