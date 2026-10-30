using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Soporte TALVEG no lee credenciales (migración <c>SoporteSinLecturaDeCredenciales</c>; OWASP ASVS 4.0.3
/// V1.4.3 y V2.10.4): <c>cae_app_soporte</c>, el rol de la conexión en Sesión Privilegiada, no tiene
/// ningún privilegio sobre <c>AspNetUserTokens</c> ni <c>CredencialesIntegracion</c>, y de
/// <c>ClavesApi</c> lee todas las columnas salvo <c>HashClave</c>.
///
/// <para>
/// Se comprueba en el catálogo (lo que está concedido, columna a columna, sobre la base migrada) y por
/// sus efectos (SQL directo y EF con el interceptor real). El catálogo cubre una columna nueva que se
/// añada mañana a esas tablas: el recuento exacto de columnas legibles de <c>ClavesApi</c> obliga a
/// decidir si la nueva es secreta.
/// </para>
/// </summary>
public class SoporteSinLecturaDeCredencialesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenant = Guid.NewGuid();
    private Guid _tenantPlataforma;
    private Guid _delegacionId;

    /// <summary>Las columnas de <c>ClavesApi</c> que Soporte TALVEG puede leer: todas menos el hash.</summary>
    private static readonly string[] ColumnasLegiblesDeClavesApi =
    [
        "Id", "NombreDescriptivo", "PrefijoVisible", "CreadaPorUsuarioId", "UltimoUsoUtc", "RevocadaEnUtc",
        "CaducaEnUtc", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado", "EliminadoEnUtc",
        "EliminadoPorUsuarioId",
    ];

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        // Una delegación de soporte del Tenant de plataforma sobre el Tenant visitado, sembrada como
        // propietario: es lo que necesita ObtenerClavesApiQueryHandler para llegar a leer ClavesApi.
        var tenantPlataforma = new Tenant("Plataforma de prueba", esPlataforma: true);
        var tenantVisitado = new Tenant("Tenant visitado de prueba");
        var delegacion = DelegacionTenant.ParaSoporte(tenantPlataforma.Id, tenantVisitado.Id);
        delegacion.ActivarParaSoporte("Prueba de integración", DateTime.UtcNow.AddHours(1), DateTime.UtcNow);

        _tenant = tenantVisitado.Id;
        _tenantPlataforma = tenantPlataforma.Id;
        _delegacionId = delegacion.Id;

        await using var contexto = CrearContexto();
        contexto.Tenants.AddRange(tenantPlataforma, tenantVisitado);
        contexto.DelegacionesTenant.Add(delegacion);
        // Sellada en el Tenant visitado por TenantSelladoInterceptor. El hash es un valor cualquiera
        // de 64 caracteres: aquí solo importa que exista la columna.
        contexto.ClavesApi.Add(new ClaveApi(
            "Integración de prueba", "hyd_prueba01", new string('a', 64), Guid.NewGuid(), DateTime.UtcNow.AddDays(30), "Prueba"));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── En el catálogo ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("AspNetUserTokens")]
    [InlineData("CredencialesIntegracion")]
    [InlineData("ClavesApi")]
    public async Task Soporte_no_tiene_privilegio_de_tabla_sobre_los_almacenes_de_secretos(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "REFERENCES", "TRIGGER" })
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT has_table_privilege('cae_app_soporte', format('public.%I', @tabla::text), @privilegio);";
            comando.Parameters.AddWithValue("tabla", tabla);
            comando.Parameters.AddWithValue("privilegio", privilegio);

            ((bool)(await comando.ExecuteScalarAsync())!).Should().BeFalse($"{privilegio} sobre {tabla}");
        }
    }

    [Theory]
    [InlineData("AspNetUserTokens")]
    [InlineData("CredencialesIntegracion")]
    public async Task Soporte_no_lee_ninguna_columna_de_tokens_ni_de_credenciales_de_integracion(string tabla)
    {
        var (legibles, todas) = await ColumnasAsync(tabla);

        todas.Should().NotBeEmpty("control del instrumento: la tabla existe y tiene columnas");
        legibles.Should().BeEmpty($"ninguna lectura de Soporte TALVEG necesita una columna de {tabla}");
    }

    [Fact]
    public async Task Soporte_lee_de_ClavesApi_todas_las_columnas_salvo_el_hash()
    {
        var (legibles, todas) = await ColumnasAsync("ClavesApi");

        todas.Should().Contain("HashClave", "control del instrumento: la columna secreta existe");
        legibles.Should().NotContain("HashClave");
        legibles.Should().BeEquivalentTo(ColumnasLegiblesDeClavesApi,
            "una columna nueva en ClavesApi no se concede sola: hay que decidir si es secreta y añadirla aquí y en la migración");
    }

    // ── Por sus efectos ────────────────────────────────────────────────────

    [Theory]
    [InlineData("SELECT \"Value\" FROM \"AspNetUserTokens\";")]
    [InlineData("SELECT count(*) FROM \"AspNetUserTokens\";")]
    [InlineData("SELECT \"RefreshToken\" FROM \"CredencialesIntegracion\";")]
    [InlineData("SELECT \"AccessToken\" FROM \"CredencialesIntegracion\";")]
    [InlineData("SELECT \"HashClave\" FROM \"ClavesApi\";")]
    [InlineData("SELECT * FROM \"ClavesApi\";")]
    public async Task Soporte_no_puede_leer_secretos_ni_con_sql_directo(string sql)
    {
        await using var conexion = await AbrirComoSoporteAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = async () => await comando.ExecuteScalarAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Soporte_lee_los_metadatos_de_las_claves_sin_el_hash()
    {
        // Control positivo: el rol no está ciego a ClavesApi, solo al hash. Es la forma de la
        // proyección de ObtenerClavesApiQuery.
        await using var conexion = await AbrirComoSoporteAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT count(*) FROM (SELECT \"Id\", \"NombreDescriptivo\", \"PrefijoVisible\", \"CreadoEnUtc\", " +
            "\"UltimoUsoUtc\", \"RevocadaEnUtc\" IS NULL, \"CaducaEnUtc\" FROM \"ClavesApi\" " +
            "WHERE NOT \"EstaEliminado\") claves;";

        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(1, "la clave sembrada en el Tenant visitado");
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_EF_proyecta_las_claves_pero_no_materializa_la_entidad()
    {
        // Por qué ObtenerClavesApiQuery proyecta RevocadaEnUtc == null y no ClaveApi.EstaActiva: una
        // propiedad sin mapear obliga a EF a traer la entidad entera, hash incluido.
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var proyectadas = await contexto.ClavesApi
            .Select(c => new { c.Id, c.NombreDescriptivo, Activa = c.RevocadaEnUtc == null, c.CaducaEnUtc })
            .ToListAsync();
        proyectadas.Should().ContainSingle().Which.Activa.Should().BeTrue();

        var materializar = async () => await contexto.ClavesApi.ToListAsync();
        (await materializar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_ObtenerClavesApiQuery_lista_sin_leer_el_hash()
    {
        // El handler real: las claves se leen por la conexión de la Sesión Privilegiada
        // (cae_app_soporte); la delegación y el Tenant de plataforma, por la del propietario. Si la
        // proyección volviera a usar ClaveApi.EstaActiva, EF traería HashClave y esto daría 42501.
        await using var contextoSoporte = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());
        await using var contextoTenants = CrearContexto();
        var handler = new ObtenerClavesApiQueryHandler(
            contextoSoporte, contextoTenants, new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantPlataforma));

        var claves = await handler.Handle(new ObtenerClavesApiQuery(_delegacionId), CancellationToken.None);

        claves.Should().ContainSingle("control positivo: el handler llegó a leer ClavesApi bajo cae_app_soporte")
            .Which.EstaActiva.Should().BeTrue();
    }

    [Fact]
    public async Task En_Sesion_Privilegiada_EF_no_lee_las_credenciales_de_integracion()
    {
        await using var contexto = CrearContexto(sesionPrivilegiadaId: Guid.NewGuid());

        var leer = async () => await contexto.CredencialesIntegracion.ToListAsync();

        (await leer.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Sin_Sesion_Privilegiada_el_runtime_sigue_leyendo_las_credenciales()
    {
        // Guarda de no regresión: la migración solo toca cae_app_soporte.
        var comoRuntime = BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion);
        await using var contexto = CrearContexto(cadenaConexion: comoRuntime);

        (await contexto.ClavesApi.ToListAsync()).Should().ContainSingle()
            .Which.HashClave.Should().HaveLength(64, "el runtime sí lee el hash: lo necesita para autenticar");
        (await contexto.CredencialesIntegracion.ToListAsync()).Should().BeEmpty();
        (await contexto.UserTokens.ToListAsync()).Should().BeEmpty();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<(List<string> Legibles, List<string> Todas)> ColumnasAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "SELECT a.attname, has_column_privilege('cae_app_soporte', a.attrelid, a.attnum, 'SELECT') " +
            "FROM pg_attribute a " +
            "WHERE a.attrelid = format('public.%I', @tabla::text)::regclass AND a.attnum > 0 AND NOT a.attisdropped;";
        comando.Parameters.AddWithValue("tabla", tabla);

        var legibles = new List<string>();
        var todas = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
        {
            todas.Add(lector.GetString(0));
            if (lector.GetBoolean(1)) legibles.Add(lector.GetString(0));
        }

        return (legibles, todas);
    }

    private async Task<NpgsqlConnection> AbrirComoSoporteAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenant);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_soporte;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private CaeManagerDbContext CrearContexto(Guid? sesionPrivilegiadaId = null, string? cadenaConexion = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaConexion ?? _cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual,
                    new ClienteActivoSeleccionadoFalso(_tenant, sesionPrivilegiadaId),
                    new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenant), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
