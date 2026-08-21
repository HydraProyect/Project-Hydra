using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// RLS de los catálogos globales de asignación, probado por comportamiento.
///
/// La propiedad que se afirma aquí <b>no</b> es "estas tablas son catálogos
/// globales" —eso describe por qué una fila enlaza dos tenants, y no dice nada
/// sobre quién queda sujeto a la política— sino esta otra, que es sobre roles:
///
/// <para>
/// <b>Las sesiones de aplicación restringidas están sujetas a RLS; las
/// operaciones sistémicas que necesitan visión global, no.</b>
/// </para>
///
/// Por eso se comprueba <b>rol por rol</b> y no leyendo
/// <c>relforcerowsecurity = false</c>: si mañana cambiara el propietario de las
/// tablas, el comportamiento podría cambiar sin que esa bandera se moviera, y un
/// test que mirase la bandera seguiría verde.
///
/// La otra mitad es la asimetría entre <c>USING</c> y <c>WITH CHECK</c>: se ve
/// por cualquiera de las dos posiciones —propietario u operador— pero solo se
/// escribe sobre el propietario contextual. Sin esa asimetría, un operador se
/// concedería a sí mismo asignaciones sobre propietarios ajenos sin necesitar
/// ver un solo dato de ellos.
/// </summary>
public class RlsCatalogosDeAsignacionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _propietario = Guid.NewGuid();
    private readonly Guid _operador = Guid.NewGuid();
    private readonly Guid _tercero = Guid.NewGuid();

    private Guid _operacionDelegadaId;
    private Guid _operacionAjenaId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;

        // El propietario delega en el operador...
        var delegada = AsignacionOperacion.Externa(
            _propietario, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora);

        // ...y un tercero delega en otro cualquiera, sin relación con los dos
        // anteriores. Es la fila que nadie de este test debería ver.
        var ajena = AsignacionOperacion.Externa(
            _tercero, Guid.NewGuid(), ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddDays(-1), vigenciaHasta: null, ahora);

        contexto.AsignacionesOperacion.AddRange(delegada, ajena);
        await contexto.SaveChangesAsync();

        _operacionDelegadaId = delegada.Id;
        _operacionAjenaId = ajena.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Invariantes 1 y 2: los roles restringidos SÍ están sujetos ─────────

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Los_roles_restringidos_estan_sujetos_a_la_politica(string rol)
    {
        // Sin ninguna de las dos variables de sesión, un rol restringido no ve
        // ni una fila. Es la comprobación de que la política le ata: si no le
        // atara, vería las dos que hay sembradas.
        await using var conexion = await AbrirComoAsync(rol, tenantActivo: null, tenantOrigen: null);

        (await ContarOperacionesAsync(conexion)).Should().Be(0);
    }

    // ── Invariante 3: el proceso sistémico conserva visión global ──────────

    [Fact]
    public async Task El_rol_propietario_conserva_la_vision_global_que_el_backfill_necesita()
    {
        // Sin SET ROLE: es el rol con el que corren hoy el seeder de backfill y
        // el job de expiración, sin tenant de sesión de ningún tipo. Si esta
        // aserción cayera, el backfill reconciliaría contra un vacío al
        // arrancar — cerraría y recrearía asignaciones en silencio.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        (await ContarOperacionesAsync(conexion)).Should().Be(2,
            "las dos filas sembradas, de dos propietarios distintos, en la misma consulta");
    }

    // ── Invariante 4 (USING): las cuatro situaciones ───────────────────────

    [Fact]
    public async Task El_propietario_ve_su_asignacion()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _propietario, _propietario);

        (await LeerIdsOperacionesAsync(conexion)).Should().BeEquivalentTo([_operacionDelegadaId]);
    }

    [Fact]
    public async Task El_operador_ve_la_asignacion_que_opera_aunque_el_workspace_activo_sea_del_propietario()
    {
        // El caso que obliga a la segunda variable. Dentro de un workspace
        // delegado, app.tenant_id es el del PROPIETARIO; con esa sola, el
        // operador no encontraría nunca "mis workspaces".
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _propietario, _operador);

        (await LeerIdsOperacionesAsync(conexion)).Should().Contain(_operacionDelegadaId);
    }

    [Fact]
    public async Task El_operador_no_ve_una_asignacion_ajena()
    {
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantActivo: null, tenantOrigen: _operador);

        var ids = await LeerIdsOperacionesAsync(conexion);

        ids.Should().Contain(_operacionDelegadaId, "la que opera sí");
        ids.Should().NotContain(_operacionAjenaId, "la de un propietario con el que no tiene ninguna relación, no");
    }

    // ── Invariante 5 (WITH CHECK): la asimetría ────────────────────────────

    [Fact]
    public async Task El_operador_no_puede_crear_una_asignacion_sobre_un_propietario_ajeno()
    {
        // Escalada por la puerta de atrás: no necesita ver nada del tercero
        // para nombrarse operador suyo. Lo corta el WITH CHECK, que exige que
        // el propietario de la fila resultante sea el del contexto.
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantActivo: null, tenantOrigen: _operador);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""AsignacionesOperacion""
    (""Id"", ""PropietarioTenantId"", ""OperadorTenantId"", ""Servicio"", ""EsRaiz"",
     ""VigenciaDesde"", ""Estado"", ""CreadoEnUtc"", ""Version"")
VALUES (gen_random_uuid(), @ajeno, @operador, 'Outbound', false, now(), 'Vigente', now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("ajeno", _tercero);
        comando.Parameters.AddWithValue("operador", _operador);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task El_operador_ve_la_asignacion_pero_no_puede_moverla_a_otro_propietario()
    {
        // El caso más importante de los siete: la fila SÍ es visible para él
        // por su posición de operador, así que el USING no lo detiene. Lo que
        // lo detiene es que el WITH CHECK no mira la fila que había sino la que
        // quedaría.
        await using var conexion = await AbrirComoAsync("cae_app_runtime", tenantActivo: null, tenantOrigen: _operador);

        (await LeerIdsOperacionesAsync(conexion)).Should().Contain(_operacionDelegadaId, "la ve, ese es el punto");

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE \"AsignacionesOperacion\" SET \"PropietarioTenantId\" = @destino WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("destino", _tercero);
        comando.Parameters.AddWithValue("id", _operacionDelegadaId);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>(
            "ver una fila por la posición de operador no puede habilitar a cambiarle el dueño"))
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task El_propietario_si_puede_escribir_sobre_su_propia_asignacion()
    {
        // Control positivo del WITH CHECK: lo que rechaza es escribir sobre un
        // propietario ajeno, no escribir. Sin esto, los dos tests de arriba
        // pasarían igual si la política prohibiera toda escritura.
        await using var conexion = await AbrirComoAsync("cae_app_runtime", _propietario, _propietario);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE \"AsignacionesOperacion\" SET \"Estado\" = 'Suspendida' WHERE \"Id\" = @id;";
        comando.Parameters.AddWithValue("id", _operacionDelegadaId);

        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<NpgsqlConnection> AbrirComoAsync(string rol, Guid? tenantActivo, Guid? tenantOrigen)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await FijarAsync(conexion, "app.tenant_id", tenantActivo);
        await FijarAsync(conexion, "app.tenant_origen_id", tenantOrigen);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = $"SET ROLE {rol};";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task FijarAsync(NpgsqlConnection conexion, string variable, Guid? valor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config(@variable, @valor, false);";
        comando.Parameters.AddWithValue("variable", variable);
        comando.Parameters.AddWithValue("valor", valor?.ToString() ?? string.Empty);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarOperacionesAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT count(*) FROM \"AsignacionesOperacion\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<List<Guid>> LeerIdsOperacionesAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT \"Id\" FROM \"AsignacionesOperacion\";";

        var ids = new List<Guid>();
        await using var lector = await consulta.ExecuteReaderAsync();
        while (await lector.ReadAsync()) ids.Add(lector.GetGuid(0));

        return ids;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
