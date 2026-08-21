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
/// RLS del plano de privilegio de plataforma, probado <b>contra un rol
/// restringido</b>.
///
/// Ese detalle no es un tecnicismo: es la diferencia entre probar algo y no
/// probar nada. El resto de la suite conecta como <c>postgres</c>, que es
/// superusuario, y un superusuario ignora RLS con <c>FORCE</c> o sin él. Una
/// batería que se limitara a sembrar y leer con la conexión por defecto pasaría
/// en verde con las políticas rotas, ausentes o diciendo justo lo contrario.
/// Por eso aquí se hace <c>SET ROLE</c> antes de cada comprobación.
///
/// La propiedad que se afirma es que la autoridad vive en los datos: se ven
/// <b>las filas que te nombran</b>, y ser usuario de plataforma no es algo que
/// la sesión declare sino algo que las filas dicen de ti. De ahí que la
/// coordenada sea <c>app.usuario_id</c> —la identidad autenticada, sin
/// afirmación de privilegio— y no una <c>app.usuario_plataforma_id</c> que
/// incrustaría esa afirmación en la conexión.
/// </summary>
public class RlsPlanoPrivilegioTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _soporteA = Guid.NewGuid();
    private readonly Guid _soporteB = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();

    private Guid _concesionDeA;
    private Guid _sesionDeA;
    private Guid _concesionDeB;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;

        var deA = ConcesionPrivilegio.SobreTenants(
            _soporteA, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        var sesionDeA = SesionPrivilegiada.Abrir(
            deA, _tenantVisitado, "Incidencia", ahora.AddMinutes(-1), TimeSpan.FromHours(1));

        var deB = ConcesionPrivilegio.SobreTenants(
            _soporteB, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));

        contexto.ConcesionesPrivilegio.AddRange(deA, deB);
        contexto.SesionesPrivilegiadas.Add(sesionDeA);
        await contexto.SaveChangesAsync();

        _concesionDeA = deA.Id;
        _sesionDeA = sesionDeA.Id;
        _concesionDeB = deB.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Guarda del propio test ─────────────────────────────────────────────

    [Fact]
    public async Task Como_superusuario_las_politicas_no_se_evaluan_y_por_eso_no_se_prueban_asi()
    {
        // Este test no comprueba una propiedad del sistema: comprueba que el
        // RESTO de tests de esta clase están montados sobre la premisa correcta.
        // Si algún día la conexión por defecto dejara de ser superusuario, este
        // se pondría rojo y avisaría de que las demás aserciones han cambiado de
        // significado sin que nadie lo note.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        (await EsSuperusuarioAsync(conexion)).Should().BeTrue(
            "la suite conecta como postgres; si eso cambia, los tests de abajo hay que releerlos");

        (await ContarConcesionesAsync(conexion)).Should().Be(2,
            "un superusuario ignora RLS aunque la tabla tenga FORCE — por eso aquí no se prueba nada");
    }

    // ── La política, contra el rol restringido ─────────────────────────────

    [Fact]
    public async Task Cada_usuario_ve_solo_las_concesiones_que_le_nombran()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_soporteA);

        var ids = await LeerIdsConcesionesAsync(conexion);

        ids.Should().BeEquivalentTo([_concesionDeA],
            "la autoridad vive en la fila: se ven las que te nombran, no las de al lado");
        ids.Should().NotContain(_concesionDeB);
    }

    [Fact]
    public async Task Sin_usuario_en_la_sesion_no_se_ve_ninguna_fila_del_plano_3()
    {
        // El caso de los procesos sin usuario: siembra al arrancar, jobs de
        // fondo, reconciliación. Ninguno necesita nada de estas tablas, y
        // ninguno lo obtiene. Fallo cerrado, y afirmado — no "es que no tenemos
        // ningún consumidor que lo haga".
        await using var conexion = await AbrirRestringidaComoAsync(usuarioId: null);

        (await ContarConcesionesAsync(conexion)).Should().Be(0);
        (await ContarAsync(conexion, "SesionesPrivilegiadas")).Should().Be(0);
        (await ContarAsync(conexion, "TenantsAlcanzadosPorConcesion")).Should().Be(0);
    }

    [Fact]
    public async Task Un_usuario_cualquiera_no_ve_nada_por_el_hecho_de_estar_autenticado()
    {
        // Identidad ≠ autoridad. Estar en app.usuario_id no concede nada: si
        // ninguna fila te nombra, no hay nada que ver.
        await using var conexion = await AbrirRestringidaComoAsync(Guid.NewGuid());

        (await ContarConcesionesAsync(conexion)).Should().Be(0);
    }

    [Fact]
    public async Task La_sesion_privilegiada_se_ve_a_traves_de_la_concesion_que_la_ampara()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_soporteA);

        (await ContarAsync(conexion, "SesionesPrivilegiadas")).Should().Be(1);

        await using var otra = await AbrirRestringidaComoAsync(_soporteB);
        (await ContarAsync(otra, "SesionesPrivilegiadas")).Should().Be(0,
            "B tiene concesión pero no es la que ampara esta sesión");
    }

    [Fact]
    public async Task El_alcance_de_una_concesion_ajena_no_es_visible()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_soporteA);

        var tenants = await LeerTenantsAlcanzadosAsync(conexion);

        tenants.Should().ContainSingle("solo la fila de alcance de SU concesión, no la de B sobre el mismo tenant");
    }

    // ── El WITH CHECK, estricto a propósito ────────────────────────────────

    [Fact]
    public async Task No_se_puede_crear_una_concesion_a_nombre_de_otro_usuario()
    {
        // Restricción deliberada de esta fase: hoy nadie escribe estas tablas,
        // así que el WITH CHECK más estricto no cuesta nada. La fase que
        // construya la apertura de sesiones tendrá que introducir de forma
        // explícita la capacidad de conceder a terceros — como decisión suya, no
        // heredada de aquí en silencio.
        await using var conexion = await AbrirRestringidaComoAsync(_soporteA);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""ConcesionesPrivilegio""
    (""Id"", ""UsuarioPlataformaId"", ""Capacidad"", ""EsAlcanceGlobal"",
     ""VigenciaDesde"", ""Estado"", ""CreadoEnUtc"", ""Version"")
VALUES (gen_random_uuid(), @otro, 'SoporteLectura', false, now(), 'Vigente', now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("otro", _soporteB);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task No_se_puede_reasignar_una_concesion_propia_a_otro_usuario()
    {
        // La ve —es suya— y aun así no puede regalarla. Mismo patrón que el
        // WITH CHECK de los otros dos planos: el USING no lo detiene, lo detiene
        // que la fila resultante ya no la nombraría a ella.
        await using var conexion = await AbrirRestringidaComoAsync(_soporteA);

        (await LeerIdsConcesionesAsync(conexion)).Should().Contain(_concesionDeA, "la ve, ese es el punto");

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE \"ConcesionesPrivilegio\" SET \"UsuarioPlataformaId\" = @otro WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("otro", _soporteB);
        comando.Parameters.AddWithValue("id", _concesionDeA);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Revocar_la_concesion_propia_si_se_permite()
    {
        // Control positivo: lo que el WITH CHECK rechaza es cambiar de dueño, no
        // escribir. Sin esto, los dos de arriba pasarían igual si la política
        // prohibiera toda escritura.
        await using var conexion = await AbrirRestringidaComoAsync(_soporteA);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE \"ConcesionesPrivilegio\" SET \"Estado\" = 'Revocada' WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", _concesionDeA);

        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> AbrirRestringidaComoAsync(Guid? usuarioId)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using (var fijar = conexion.CreateCommand())
        {
            fijar.CommandText = "SELECT set_config('app.usuario_id', @valor, false);";
            fijar.Parameters.AddWithValue("valor", usuarioId?.ToString() ?? string.Empty);
            await fijar.ExecuteNonQueryAsync();
        }

        // Imprescindible: sin SET ROLE seguiríamos siendo superusuario y las
        // políticas no se evaluarían.
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task<bool> EsSuperusuarioAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT rolsuper FROM pg_roles WHERE rolname = current_user;";
        return (bool)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<long> ContarConcesionesAsync(NpgsqlConnection conexion) =>
        await ContarAsync(conexion, "ConcesionesPrivilegio");

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<List<Guid>> LeerIdsConcesionesAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT \"Id\" FROM \"ConcesionesPrivilegio\";";

        var ids = new List<Guid>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync()) ids.Add(lector.GetGuid(0));

        return ids;
    }

    private static async Task<List<Guid>> LeerTenantsAlcanzadosAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT \"TenantId\" FROM \"TenantsAlcanzadosPorConcesion\";";

        var ids = new List<Guid>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync()) ids.Add(lector.GetGuid(0));

        return ids;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantVisitado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
