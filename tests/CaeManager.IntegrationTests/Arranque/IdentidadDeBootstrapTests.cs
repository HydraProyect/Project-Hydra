using System.Data.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>Por qué el arranque necesita una identidad administrativa</b> — demostrado
/// por efecto, no por lectura del código.
///
/// <para>
/// La frontera que introdujo #257 se justificó con dos fallos: uno observado en
/// staging el 2026-08-23 y otro predicho por análisis. Estos tests reproducen
/// <b>ambos</b> bajo el rol restringido real, y comprueban que la identidad de
/// bootstrap los resuelve. Sin ellos, la clasificación de los seeders sería una
/// afirmación estructural sin evidencia de comportamiento.
/// </para>
///
/// <para>
/// El rol restringido se adopta con <c>SET ROLE</c> y no abriendo una conexión de
/// login, por el mismo motivo que <c>AislamientoRlsPostgresTests</c>: el
/// superusuario de los tests puede asumir cualquier rol sin contraseña, y una vez
/// asumido PostgreSQL aplica RLS exactamente igual que a una conexión real.
/// </para>
/// </summary>
public class IdentidadDeBootstrapTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _raiz = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContextoDeBootstrap();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Fallo 1: la ceguera de lectura que mató a staging ──────────────────

    /// <summary>
    /// El defecto exacto: la fila existe, pero para el rol restringido sin
    /// <c>app.usuario_id</c> la tabla está vacía. Con control negativo, para que
    /// el cero no se pueda confundir con "no hay fila".
    /// </summary>
    [Fact]
    public async Task La_fila_de_bootstrap_es_invisible_para_el_rol_restringido_sin_usuario_de_sesion()
    {
        await SembrarFilaDeBootstrapAsync();

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        (await ContarEstadoAsync(conexion)).Should().Be(1,
            "control negativo: como propietario la fila SÍ se ve, así que un cero más abajo significa " +
            "que RLS la está ocultando y no que no exista");

        await AdoptarRolRestringidoAsync(conexion);

        (await ContarEstadoAsync(conexion)).Should().Be(0,
            "la política de SELECT exige UsuarioRaizId = app.usuario_id, y en el arranque no hay sesión " +
            "de usuario: para el rol restringido la tabla está vacía aunque la fila exista");
    }

    /// <summary>
    /// La consecuencia, que es el crash-loop observado: la guarda "si no existe,
    /// créala" entra siempre, y el <c>INSERT</c> sí ve la clave primaria.
    /// </summary>
    [Fact]
    public async Task Y_por_eso_el_segundo_arranque_bajo_ese_rol_choca_con_la_clave_primaria()
    {
        await SembrarFilaDeBootstrapAsync();

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await AdoptarRolRestringidoAsync(conexion);

        // Exactamente lo que hace IdentitySeeder: mira si hay fila —no la ve— y
        // por tanto inserta.
        (await ContarEstadoAsync(conexion)).Should().Be(0);

        await using var contexto = CrearContexto(tenantDeSesion: null, adoptarRol: true);
        contexto.EstadoBootstrapPlataforma.Add(
            EstadoBootstrapPlataforma.Designar(Guid.NewGuid(), DateTime.UtcNow));

        var guardar = async () => await contexto.SaveChangesAsync();

        var fallo = await guardar.Should().ThrowAsync<DbUpdateException>();

        fallo.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation,
                "es el 23505 con el que staging entró en crash-loop: la lectura está filtrada por RLS, " +
                "la clave primaria no");
    }

    /// <summary>
    /// Y la mitad que cierra el caso: con la identidad de bootstrap la fila se ve,
    /// así que la guarda funciona y no se intenta duplicar nada.
    /// </summary>
    [Fact]
    public async Task La_identidad_de_bootstrap_si_ve_la_fila_existente()
    {
        await SembrarFilaDeBootstrapAsync();

        await using var contexto = CrearContextoDeBootstrap();
        var estado = await contexto.EstadoBootstrapPlataforma.FirstOrDefaultAsync();

        estado.Should().NotBeNull(
            "es la lectura que hace IdentitySeeder: si la ve, no entra en la rama que inserta");
        estado!.UsuarioRaizId.Should().Be(_raiz);
    }

    // ── Fallo 2: el backfill es cross-tenant y RLS es per-tenant ───────────

    /// <summary>
    /// Con la identidad administrativa, el backfill completa <b>los seis</b>
    /// tenants. Seis porque es lo que tiene producción: el número no es
    /// decorativo, es el que hace que el fallo del test siguiente sea visible.
    /// </summary>
    [Fact]
    public async Task El_backfill_con_seis_tenants_completa_las_seis_operaciones_raiz()
    {
        var tenants = await SembrarTenantsAsync(6);

        await using (var contexto = CrearContextoDeBootstrap())
            await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);

        await using var comprobacion = CrearContextoDeBootstrap();
        var raices = await comprobacion.AsignacionesOperacion
            .Where(o => o.EsRaiz)
            .Select(o => o.PropietarioTenantId)
            .ToListAsync();

        var todosLosTenants = await comprobacion.Tenants.Select(t => t.Id).ToListAsync();

        todosLosTenants.Should().Contain(tenants,
            "guarda del propio test: los seis que siembra tienen que estar, o no estaría midiendo lo que dice");

        raices.Should().BeEquivalentTo(todosLosTenants,
            "la raíz es el ancla de cada tenant —incluido el tenant #1 que siembra la migración—: si falta " +
            "una, ese tenant se queda sin reparto operativo");
    }

    /// <summary>
    /// La prueba adversaria: el mismo seeder, los mismos datos, bajo el rol
    /// restringido y <b>con <c>app.tenant_id</c> correctamente fijado</b> — que es
    /// la situación de producción, no una degradada.
    ///
    /// <para>
    /// Falla igualmente, y ese es el punto: el backfill escribe una fila por cada
    /// tenant en un solo <c>SaveChanges</c>, y la política exige
    /// <c>PropietarioTenantId = app.tenant_id</c>. Ninguna elección de tenant
    /// puede satisfacer a las seis a la vez. No es un ámbito mal fijado: es una
    /// operación cross-tenant bajo una identidad per-tenant.
    /// </para>
    /// </summary>
    [Fact]
    public async Task El_mismo_backfill_bajo_el_rol_restringido_no_puede_completarse()
    {
        var tenants = await SembrarTenantsAsync(6);

        await using var contexto = CrearContextoRestringido(tenants[0]);

        var ejecutar = async () =>
            await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);

        await ejecutar.Should().ThrowAsync<Exception>(
            "con app.tenant_id fijado a UN tenant, las filas de los otros cinco violan el WITH CHECK de " +
            "posicion_en_la_asignacion; y no hay ningún valor que sirva para las seis a la vez");

        await using var comprobacion = CrearContextoDeBootstrap();
        (await comprobacion.AsignacionesOperacion.CountAsync(o => o.EsRaiz)).Should().Be(0,
            "el SaveChanges es único, así que o entran las seis o no entra ninguna");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task SembrarFilaDeBootstrapAsync()
    {
        await using var contexto = CrearContextoDeBootstrap();
        contexto.EstadoBootstrapPlataforma.Add(EstadoBootstrapPlataforma.Designar(_raiz, DateTime.UtcNow));
        await contexto.SaveChangesAsync();
    }

    private async Task<List<Guid>> SembrarTenantsAsync(int cuantos)
    {
        await using var contexto = CrearContextoDeBootstrap();
        var ids = new List<Guid>();

        for (var i = 0; i < cuantos; i++)
        {
            var tenant = new Tenant($"Tenant de prueba {i}");
            contexto.Tenants.Add(tenant);
            ids.Add(tenant.Id);
        }

        await contexto.SaveChangesAsync();
        return ids;
    }

    private static async Task AdoptarRolRestringidoAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SET ROLE cae_app_runtime;";
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<int> ContarEstadoAsync(NpgsqlConnection conexion)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT count(*) FROM ""EstadoBootstrapPlataforma"";";
        return Convert.ToInt32(await comando.ExecuteScalarAsync());
    }

    /// <summary>Identidad administrativa: el rol propietario, sin adoptar nada.</summary>
    private CaeManagerDbContext CrearContextoDeBootstrap() =>
        CrearContexto(tenantDeSesion: null, adoptarRol: false);

    /// <summary>
    /// Identidad de tráfico: adopta <c>cae_app_runtime</c> y fija
    /// <c>app.tenant_id</c> en cada apertura, como hace el interceptor real.
    /// </summary>
    private CaeManagerDbContext CrearContextoRestringido(Guid tenantDeSesion) =>
        CrearContexto(tenantDeSesion, adoptarRol: true);

    private CaeManagerDbContext CrearContexto(Guid? tenantDeSesion, bool adoptarRol)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));

        if (adoptarRol)
            opciones.AddInterceptors(new AdoptarRolRestringido(tenantDeSesion));

        return new CaeManagerDbContext(
            opciones.Options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }

    /// <summary>
    /// Reproduce lo que hace <c>TenantRlsConnectionInterceptor</c> en producción,
    /// reducido a lo que estos tests necesitan: fijar la coordenada de tenant y
    /// adoptar el rol restringido, en ese orden.
    /// </summary>
    private sealed class AdoptarRolRestringido(Guid? tenant) : DbConnectionInterceptor
    {
        public override void ConnectionOpened(DbConnection conexion, ConnectionEndEventData datos)
        {
            Preparar(conexion);
            base.ConnectionOpened(conexion, datos);
        }

        public override Task ConnectionOpenedAsync(
            DbConnection conexion, ConnectionEndEventData datos, CancellationToken cancellationToken = default)
        {
            Preparar(conexion);
            return base.ConnectionOpenedAsync(conexion, datos, cancellationToken);
        }

        private void Preparar(DbConnection conexion)
        {
            using var comando = conexion.CreateCommand();

            if (tenant is { } valor)
            {
                comando.CommandText = "SELECT set_config('app.tenant_id', @tenant, false); SET ROLE cae_app_runtime;";
                var parametro = comando.CreateParameter();
                parametro.ParameterName = "tenant";
                parametro.Value = valor.ToString();
                comando.Parameters.Add(parametro);
            }
            else
            {
                // Sin coordenada de tenant: es el contexto del arranque, que no
                // tiene sesión de usuario ni workspace.
                comando.CommandText = "SET ROLE cae_app_runtime;";
            }

            comando.ExecuteNonQuery();
        }
    }
}
