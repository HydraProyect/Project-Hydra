using CaeManager.Application.Common;
using CaeManager.Application.Plataforma.OrdenMenu;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Orden global del menú lateral (decisión del propietario de 2026-09-23) contra PostgreSQL real
/// y <b>conectando como <c>cae_app_runtime</c></b>, con los interceptores de producción: conectar
/// como propietario no ejercitaría la RLS de la tabla.
///
/// <para>
/// Tabla de plataforma, sin TenantId (precedente: <c>EstadoBootstrapPlataforma</c>). Tres
/// propiedades, cada una en la capa que la garantiza:
/// </para>
/// <list type="bullet">
///   <item>Application: un Administrador de Tenant no puede escribir (el handler lo rechaza).</item>
///   <item>PostgreSQL/RLS: aunque alguien se saltara Application, la base rechaza la escritura de
///   quien no tenga una concesión AdminPlataforma <b>global</b>; nadie puede borrar la fila.</item>
///   <item>Composición: lo que guarda el Actor de Plataforma TALVEG lo leen usuarios de dos Tenants
///   distintos, y la auditoría registra el Actor real.</item>
/// </list>
/// </summary>
public class OrdenMenuLateralIntegrationTests : IAsyncLifetime
{
    private readonly string _cadenaPropietario = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorPlataforma = Guid.NewGuid();
    private readonly Guid _adminAcotado = Guid.NewGuid();
    private readonly Guid _administradorDeTenant = Guid.NewGuid();
    private readonly Guid _usuarioTenantA = Guid.NewGuid();
    private readonly Guid _usuarioTenantB = Guid.NewGuid();
    private Guid _tenantPlataforma;
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContextoPropietario();
        await contexto.Database.MigrateAsync();

        var plataforma = new Tenant("Plataforma de prueba", esPlataforma: true);
        var a = new Tenant($"Tenant A {Guid.NewGuid():N}");
        var b = new Tenant($"Tenant B {Guid.NewGuid():N}");
        contexto.Tenants.AddRange(plataforma, a, b);

        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.AddRange(
            ConcesionPrivilegio.Global(_actorPlataforma, vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: null),
            ConcesionPrivilegio.SobreTenants(
                _adminAcotado, CapacidadPrivilegio.AdminPlataforma, [a.Id],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddDays(30)));
        await contexto.SaveChangesAsync();

        (_tenantPlataforma, _tenantA, _tenantB) = (plataforma.Id, a.Id, b.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaPropietario);

    // ── Application ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Un_Administrador_de_Tenant_no_puede_guardar_el_orden()
    {
        await using var sesion = Sesion(_administradorDeTenant, _tenantA);

        var resultado = await sesion.Guardar(new GuardarOrdenMenuLateralCommand(["plataforma"], [], Guid.Empty));

        resultado.Error.Codigo.Should().Be("OrdenMenu.SinPermiso");
        (await ContarFilasAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Una_concesion_AdminPlataforma_acotada_a_un_Tenant_tampoco_puede()
    {
        await using var sesion = Sesion(_adminAcotado, _tenantA);

        var resultado = await sesion.Guardar(new GuardarOrdenMenuLateralCommand(["plataforma"], [], Guid.Empty));

        resultado.Error.Codigo.Should().Be("OrdenMenu.SinPermiso");
        (await ContarFilasAsync()).Should().Be(0);
    }

    // ── Composición: dos Tenants y auditoría ───────────────────────────────

    [Fact]
    public async Task Lo_que_guarda_el_Actor_de_Plataforma_lo_leen_usuarios_de_dos_Tenants_y_queda_auditado()
    {
        await using (var sesion = Sesion(_actorPlataforma, _tenantPlataforma))
        {
            var resultado = await sesion.Guardar(
                new GuardarOrdenMenuLateralCommand(["plataforma", "control"], ["conectores-cae"], Guid.Empty));
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        }

        foreach (var (usuario, tenant) in new[] { (_usuarioTenantA, _tenantA), (_usuarioTenantB, _tenantB) })
        {
            await using var lector = Sesion(usuario, tenant);
            var orden = await lector.Leer();

            orden.Should().NotBeNull($"el usuario del Tenant {tenant} lee la fila global como cae_app_runtime");
            orden!.Grupos.Should().Equal("plataforma", "control");
            orden.Enlaces.Should().Equal("conectores-cae");
            orden.ActualizadoPorUsuarioId.Should().Be(_actorPlataforma);
        }

        await using var propietario = CrearContextoPropietario();
        var auditoria = await propietario.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.EntidadTipo == nameof(OrdenMenuLateral))
            .ToListAsync();
        auditoria.Should().ContainSingle().Which.ActorRealUsuarioId.Should().Be(_actorPlataforma,
            "cada cambio se audita con el Actor real");
    }

    // ── PostgreSQL/RLS ──────────────────────────────────────────────────────

    [Fact]
    public async Task La_RLS_rechaza_el_alta_de_quien_no_tiene_concesion_global_aunque_se_salte_Application()
    {
        foreach (var usuario in new[] { _administradorDeTenant, _adminAcotado })
        {
            await using var conexion = await AbrirRuntimeComoAsync(usuario);
            var accion = async () => await InsertarFilaAsync(conexion, usuario);

            (await accion.Should().ThrowAsync<PostgresException>())
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege,
                    "solo una concesión AdminPlataforma GLOBAL decide algo que ven todos los Tenants");
        }

        await using var global = await AbrirRuntimeComoAsync(_actorPlataforma);
        (await InsertarFilaAsync(global, _actorPlataforma)).Should().Be(1,
            "control positivo: la misma sentencia con concesión global sí entra");
    }

    [Fact]
    public async Task La_RLS_no_deja_cambiar_la_fila_sin_concesion_global_ni_borrarla_a_nadie()
    {
        await using (var global = await AbrirRuntimeComoAsync(_actorPlataforma))
            await InsertarFilaAsync(global, _actorPlataforma);

        await using (var acotado = await AbrirRuntimeComoAsync(_adminAcotado))
        {
            (await EjecutarAsync(acotado, @"UPDATE ""OrdenMenuLateral"" SET ""OrdenGrupos"" = ARRAY['negocio'];"))
                .Should().Be(0, "sin concesión global la fila no es actualizable: el USING la oculta al UPDATE");
            (await EjecutarAsync(acotado, @"SELECT count(*) FROM ""OrdenMenuLateral"";", escalar: true))
                .Should().Be(1, "pero sí la lee: la lectura es de todos");
        }

        await using (var global = await AbrirRuntimeComoAsync(_actorPlataforma))
        {
            (await EjecutarAsync(global, @"UPDATE ""OrdenMenuLateral"" SET ""OrdenGrupos"" = ARRAY['negocio'];"))
                .Should().Be(1, "control positivo del UPDATE");
            (await EjecutarAsync(global, @"DELETE FROM ""OrdenMenuLateral"";"))
                .Should().Be(0, "sin política de DELETE nadie la borra; restablecer es guardar listas vacías");
        }

        (await ContarFilasAsync()).Should().Be(1);
    }

    // ── Arnés ───────────────────────────────────────────────────────────────

    private SesionRuntime Sesion(Guid usuarioId, Guid tenantId) => new(_cadenaPropietario, usuarioId, tenantId);

    /// <summary>
    /// Un contexto como el de una petición real: conecta como <c>cae_app_runtime</c> y lleva los
    /// cuatro interceptores de producción (sesión RLS con <c>app.usuario_id</c>, sellado, auditoría
    /// y concurrencia), mismo cableado que <c>ArnesDeArranqueRuntime</c>, pero con usuario.
    /// </summary>
    private sealed class SesionRuntime : IAsyncDisposable
    {
        private readonly ServiceProvider _servicios;
        private readonly AsyncServiceScope _ambito;
        private readonly CaeManagerDbContext _contexto;
        private readonly Guid _usuarioId;

        public SesionRuntime(string cadenaPropietario, Guid usuarioId, Guid tenantId)
        {
            _usuarioId = usuarioId;
            var servicios = new ServiceCollection();
            servicios.AddLogging();
            servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            servicios.AddSingleton<ITenantActual>(new TenantActualAmbiental { TenantId = tenantId });
            servicios.AddSingleton<IClienteActivoSeleccionado>(new SinClienteActivo());
            servicios.AddSingleton<ICurrentUserService>(new UsuarioFijo(usuarioId, tenantId));
            servicios.AddSingleton<IActorAuditoria>(new ActorFijo(ActorAuditoria.Normal(usuarioId)));
            servicios.AddScoped<AuditoriaInterceptor>();
            servicios.AddScoped<TenantSelladoInterceptor>();
            servicios.AddScoped<TenantRlsConnectionInterceptor>();
            servicios.AddSingleton<ConcurrenciaOptimistaInterceptor>();
            servicios.AddDbContext<CaeManagerDbContext>((sp, opciones) =>
                ConfiguracionDeContexto.Aplicar(
                    opciones, sp, BaseDatosPostgresDePruebas.CadenaComoRuntime(cadenaPropietario)));

            _servicios = servicios.BuildServiceProvider();
            _ambito = _servicios.CreateAsyncScope();
            _contexto = _ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        }

        public Task<CaeManager.Domain.Common.Result> Guardar(GuardarOrdenMenuLateralCommand comando) =>
            new GuardarOrdenMenuLateralCommandHandler(
                new OrdenMenuLateralRepository(_contexto),
                new AutorizacionAdminPlataformaPorConcesion(_contexto),
                _ambito.ServiceProvider.GetRequiredService<ICurrentUserService>(),
                _ambito.ServiceProvider.GetRequiredService<IActorAuditoria>(),
                _contexto,
                new CacheOrdenMenuLateral())
            .Handle(comando, CancellationToken.None);

        // Caché nueva a propósito: se mide lo que ve cada Tenant en la base, no lo que
        // recuerda el proceso.
        public Task<OrdenMenuLateralDto?> Leer() =>
            new ObtenerOrdenMenuLateralQueryHandler(new OrdenMenuLateralRepository(_contexto), new CacheOrdenMenuLateral())
                .Handle(new ObtenerOrdenMenuLateralQuery(), CancellationToken.None);

        public async ValueTask DisposeAsync()
        {
            await _ambito.DisposeAsync();
            await _servicios.DisposeAsync();
        }
    }

    private async Task<NpgsqlConnection> AbrirRuntimeComoAsync(Guid usuarioId)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaPropietario));
        await conexion.OpenAsync();
        await using var fijar = conexion.CreateCommand();
        fijar.CommandText = "SELECT set_config('app.usuario_id', @valor, false);";
        fijar.Parameters.AddWithValue("valor", usuarioId.ToString());
        await fijar.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<int> InsertarFilaAsync(NpgsqlConnection conexion, Guid actor)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
INSERT INTO ""OrdenMenuLateral"" (""Id"", ""OrdenGrupos"", ""OrdenEnlaces"", ""ActualizadoPorUsuarioId"", ""ActualizadoEnUtc"", ""Version"")
VALUES (@id, ARRAY['control'], ARRAY[]::text[], @actor, now(), gen_random_uuid());";
        comando.Parameters.AddWithValue("id", OrdenMenuLateral.ClaveCanonica);
        comando.Parameters.AddWithValue("actor", actor);
        return await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> EjecutarAsync(NpgsqlConnection conexion, string sql, bool escalar = false)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return escalar ? (long)(await comando.ExecuteScalarAsync())! : await comando.ExecuteNonQueryAsync();
    }

    private async Task<int> ContarFilasAsync()
    {
        await using var contexto = CrearContextoPropietario();
        return await contexto.OrdenMenuLateral.CountAsync();
    }

    private CaeManagerDbContext CrearContextoPropietario()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental());
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class UsuarioFijo(Guid usuarioId, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}
