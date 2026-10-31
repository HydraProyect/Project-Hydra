using CaeManager.Application.Common;
using CaeManager.Domain.Seguridad;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// <c>cae_app_soporte</c> no lee <c>SesionesUsuario.Sid</c> (OWASP ASVS 4.0.3 V4.1.3), y la revalidación de la
/// sesión sigue funcionando con ese rol: va por <c>app_sesion_vigente</c>, que es SECURITY DEFINER. Lo mide en
/// la base migrada, con una fila real: <c>has_column_privilege</c> incluye herencia de roles y <c>PUBLIC</c>, de
/// modo que un grant de tabla que anulara la revocación por columna pondría en rojo el primer test.
/// </summary>
public class SoporteSinSidSesionUsuarioTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly Guid _sid = Guid.NewGuid();
    private Guid _tenant;
    private Guid _sesion;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);
        var tenant = new Tenant("Tenant de prueba");
        _tenant = tenant.Id;
        await using var contexto = CrearContexto();
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        var sesion = new SesionUsuario(_usuario, _sid, DateTime.UtcNow);
        contexto.SesionesUsuario.Add(sesion);
        await contexto.SaveChangesAsync();
        _sesion = sesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Soporte_solo_lee_las_columnas_permitidas()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand("""
            SELECT a.attname, has_column_privilege('cae_app_soporte', a.attrelid, a.attnum, 'SELECT')
            FROM pg_attribute a
            WHERE a.attrelid = 'public."SesionesUsuario"'::regclass AND a.attnum > 0 AND NOT a.attisdropped;
            """, conexion);
        var todas = new List<string>();
        var legibles = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            todas.Add(lector.GetString(0));
            if (lector.GetBoolean(1)) legibles.Add(lector.GetString(0));
        }
        todas.Should().Contain("Sid");
        legibles.Should().BeEquivalentTo(todas.Where(c => c != "Sid"));
        legibles.Should().BeEquivalentTo(new[] { "Id", "UsuarioId", "EmitidaEnUtc", "RevocadaEnUtc", "TenantId" });
    }

    [Theory]
    [InlineData("SELECT \"Sid\" FROM \"SesionesUsuario\";")]
    [InlineData("SELECT * FROM \"SesionesUsuario\";")]
    public async Task Soporte_recibe_permiso_denegado_al_leer_sid(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenant);
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_soporte;";
        await setRol.ExecuteNonQueryAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        var leer = async () => await comando.ExecuteScalarAsync();
        (await leer.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Revalidacion_y_proyeccion_funcionan_en_sesion_privilegiada()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var fila = await contexto.SesionesUsuario.Select(s => new { s.Id, s.UsuarioId, s.EmitidaEnUtc, s.RevocadaEnUtc, s.TenantId }).SingleAsync();
        fila.Id.Should().Be(_sesion);
        fila.UsuarioId.Should().Be(_usuario);
        fila.TenantId.Should().Be(_tenant);
        fila.RevocadaEnUtc.Should().BeNull();

        var registro = new RegistroSesionesUsuario(contexto, null!, null!, NullLogger<RegistroSesionesUsuario>.Instance);
        (await registro.EstaVigenteAsync(_usuario, _sid)).Should().BeTrue();
        (await registro.EstaVigenteAsync(_usuario, Guid.NewGuid())).Should().BeFalse();

        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var revocar = new NpgsqlCommand("UPDATE \"SesionesUsuario\" SET \"RevocadaEnUtc\" = now() WHERE \"Id\" = @id", propietario);
        revocar.Parameters.AddWithValue("id", _sesion);
        (await revocar.ExecuteNonQueryAsync()).Should().Be(1);
        (await registro.EstaVigenteAsync(_usuario, _sid)).Should().BeFalse();
    }

    [Fact]
    public async Task Materializar_la_entidad_falla_solo_en_Sesion_Privilegiada()
    {
        // Guarda de no regresión: la migración solo toca cae_app_soporte; el runtime sigue leyendo la fila.
        await using var contextoRuntime = CrearContexto(cadena: BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena));
        (await contextoRuntime.SesionesUsuario.ToListAsync()).Should().ContainSingle(
            "control del instrumento: sin Sesión Privilegiada la fila existe y se materializa");

        await using var contextoSoporte = CrearContexto(Guid.NewGuid());
        var materializar = async () => await contextoSoporte.SesionesUsuario.ToListAsync();
        (await materializar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private CaeManagerDbContext CrearContexto(Guid? sesionPrivilegiadaId = null, string? cadena = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadena ?? _cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new TenantRlsConnectionInterceptor(
                tenantActual, new Seleccion(_tenant, sesionPrivilegiadaId),
                new CurrentUserServiceFalso(_usuario, tenantOrigenId: _tenant), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class Seleccion(Guid tenantId, Guid? sesionId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionId;
    }
}
