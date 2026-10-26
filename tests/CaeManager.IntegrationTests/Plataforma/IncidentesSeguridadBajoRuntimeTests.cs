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
/// Incidentes de seguridad sospechados (plano de Plataforma), contra el rol restringido
/// <c>cae_app_runtime</c> con <c>SET ROLE</c>: como superusuario las políticas no se
/// evalúan. Se afirma que cualquier usuario puede disparar el registro por la función,
/// que no puede leer ni escribir la tabla por su cuenta, que solo un AdminPlataforma
/// global la lee, y que la función deduplica.
/// </summary>
public class IncidentesSeguridadBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _usuarioDelTenant = Guid.NewGuid();
    private readonly Guid _adminPlataformaGlobal = Guid.NewGuid();
    private readonly Guid _adminPlataformaPorTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        contexto.ConcesionesPrivilegio.AddRange(
            ConcesionPrivilegio.Global(_adminPlataformaGlobal, ahora.AddMinutes(-10), ahora.AddHours(4)),
            ConcesionPrivilegio.SobreTenants(_adminPlataformaPorTenant, CapacidadPrivilegio.AdminPlataforma, [_tenantA],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4)));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_usuario_cualquiera_registra_por_la_funcion_pero_no_ve_el_incidente()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);

        var (id, nuevo) = await RegistrarAsync(conexion, "InicioSesionNoReconocido", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        nuevo.Should().BeTrue();
        (await ContarAsync(conexion, "IncidentesSeguridadSospechados")).Should().Be(0,
            "quien dispara la señal no lee los incidentes: el plano de Plataforma no es de su Tenant");
        (await ContarAsync(conexion, "TransicionesIncidenteSeguridad")).Should().Be(0);

        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """
            SELECT i."Estado", i."Detecciones", i."TenantsAfectados", i."ConfirmadoEnUtc" IS NULL,
                   t."EstadoAnterior" IS NULL, t."EstadoNuevo", t."ActorUsuarioId" IS NULL
            FROM "IncidentesSeguridadSospechados" i JOIN "TransicionesIncidenteSeguridad" t ON t."IncidenteId" = i."Id"
            WHERE i."Id" = @id
            """;
        consulta.Parameters.AddWithValue("id", id);
        await using var lector = await consulta.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetString(0).Should().Be("Sospechado");
        lector.GetInt32(1).Should().Be(1);
        lector.GetFieldValue<Guid[]>(2).Should().Equal(_tenantA);
        lector.GetBoolean(3).Should().BeTrue("la hora de confirmación nace nula: el plazo no corre hasta confirmar");
        lector.GetBoolean(4).Should().BeTrue();
        lector.GetString(5).Should().Be("Sospechado");
        lector.GetBoolean(6).Should().BeTrue("la primera transición la hace el sistema");
        (await lector.ReadAsync()).Should().BeFalse("una sola transición al nacer");
    }

    [Fact]
    public async Task La_misma_senal_sobre_el_mismo_sujeto_en_la_ventana_suma_una_deteccion_y_une_los_Tenants()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var sujeto = $"usuario:{_usuarioDelTenant:D}";

        var (primero, _) = await RegistrarAsync(conexion, "AccesoRepetidoAOtroTenant", sujeto, [_tenantA]);
        var (segundo, nuevo) = await RegistrarAsync(conexion, "AccesoRepetidoAOtroTenant", sujeto, [_tenantB, _tenantA]);
        var (otraSenal, otraNueva) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);
        var (otroSujeto, otroNuevo) = await RegistrarAsync(conexion, "AccesoRepetidoAOtroTenant", $"usuario:{Guid.NewGuid():D}", [_tenantA]);

        segundo.Should().Be(primero);
        nuevo.Should().BeFalse();
        otraNueva.Should().BeTrue();
        otraSenal.Should().NotBe(primero);
        otroNuevo.Should().BeTrue();
        otroSujeto.Should().NotBe(primero);

        await using var superusuario = await AbrirAsync();
        await using var consulta = superusuario.CreateCommand();
        consulta.CommandText = """SELECT "Detecciones", "TenantsAfectados" FROM "IncidentesSeguridadSospechados" WHERE "Id" = @id""";
        consulta.Parameters.AddWithValue("id", primero);
        await using var lector = await consulta.ExecuteReaderAsync();
        await lector.ReadAsync();
        lector.GetInt32(0).Should().Be(2);
        lector.GetFieldValue<Guid[]>(1).Should().BeEquivalentTo([_tenantA, _tenantB]);
    }

    [Fact]
    public async Task Un_incidente_descartado_no_absorbe_la_deteccion_siguiente()
    {
        await using var conexion = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA);
        var sujeto = $"usuario:{_usuarioDelTenant:D}";
        var (primero, _) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);

        await using (var superusuario = await AbrirAsync())
        await using (var descartar = superusuario.CreateCommand())
        {
            descartar.CommandText = """UPDATE "IncidentesSeguridadSospechados" SET "Estado" = 'Descartado' WHERE "Id" = @id""";
            descartar.Parameters.AddWithValue("id", primero);
            await descartar.ExecuteNonQueryAsync();
        }

        var (segundo, nuevo) = await RegistrarAsync(conexion, "ExtraccionMasiva", sujeto, [_tenantA]);
        nuevo.Should().BeTrue();
        segundo.Should().NotBe(primero);
    }

    [Fact]
    public async Task Solo_un_AdminPlataforma_global_lee_los_incidentes()
    {
        await using (var disparo = await AbrirRestringidaComoAsync(_usuarioDelTenant, _tenantA))
            await RegistrarAsync(disparo, "InicioSesionNoReconocido", $"usuario:{_usuarioDelTenant:D}", [_tenantA]);

        await using var global = await AbrirRestringidaComoAsync(_adminPlataformaGlobal, tenantId: null);
        (await ContarAsync(global, "IncidentesSeguridadSospechados")).Should().Be(1);
        (await ContarAsync(global, "TransicionesIncidenteSeguridad")).Should().Be(1);

        await using var porTenant = await AbrirRestringidaComoAsync(_adminPlataformaPorTenant, tenantId: null);
        (await ContarAsync(porTenant, "IncidentesSeguridadSospechados")).Should().Be(0,
            "un incidente cruza Tenants: una concesión acotada a un Tenant no lo abre");

        await using var sinUsuario = await AbrirRestringidaComoAsync(usuarioId: null, tenantId: null);
        (await ContarAsync(sinUsuario, "IncidentesSeguridadSospechados")).Should().Be(0);
    }

    [Theory]
    [InlineData("""INSERT INTO "IncidentesSeguridadSospechados" ("Id", "Senal", "SujetoClave", "DetectadoEnUtc", "UltimaDeteccionEnUtc", "Detecciones", "TenantsAfectados", "Evidencia", "Estado") VALUES (gen_random_uuid(), 'ExtraccionMasiva', 'x', now(), now(), 1, '{}', '{}', 'Sospechado')""")]
    [InlineData("""UPDATE "IncidentesSeguridadSospechados" SET "Estado" = 'Descartado'""")]
    [InlineData("""DELETE FROM "TransicionesIncidenteSeguridad" """)]
    public async Task Ni_siquiera_el_AdminPlataforma_global_escribe_las_tablas_directamente(string sql)
    {
        await using var conexion = await AbrirRestringidaComoAsync(_adminPlataformaGlobal, tenantId: null);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var accion = () => comando.ExecuteNonQueryAsync();
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Las_dos_tablas_tienen_RLS_forzado_y_solo_la_politica_de_lectura()
    {
        await using var conexion = await AbrirAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT c.relname, c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT string_agg(p.polname || ':' || p.polcmd, ',' ORDER BY p.polname) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'public' AND c.relname IN ('IncidentesSeguridadSospechados', 'TransicionesIncidenteSeguridad')
            """;
        var filas = new List<(string Tabla, bool Rls, bool Force, string? Politicas)>();
        await using (var lector = await comando.ExecuteReaderAsync())
        {
            while (await lector.ReadAsync())
                filas.Add((lector.GetString(0), lector.GetBoolean(1), lector.GetBoolean(2), lector.IsDBNull(3) ? null : lector.GetString(3)));
        }

        filas.Should().HaveCount(2);
        filas.Should().OnlyContain(f => f.Rls && f.Force && f.Politicas == "actor_de_plataforma_lee:r");
    }

    private static async Task<(Guid Id, bool Nuevo)> RegistrarAsync(NpgsqlConnection conexion, string senal, string sujeto, Guid[] tenants)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT incidente_id, es_nuevo
            FROM app_registrar_incidente_seguridad(@id, @transicion, @senal, @sujeto, @tenants, '{"recuento": 1}'::jsonb, 3600)
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("transicion", Guid.NewGuid());
        comando.Parameters.AddWithValue("senal", senal);
        comando.Parameters.AddWithValue("sujeto", sujeto);
        comando.Parameters.AddWithValue("tenants", tenants);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        return (lector.GetGuid(0), lector.GetBoolean(1));
    }

    private async Task<NpgsqlConnection> AbrirAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        return conexion;
    }

    private async Task<NpgsqlConnection> AbrirRestringidaComoAsync(Guid? usuarioId, Guid? tenantId)
    {
        var conexion = await AbrirAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenantId, usuarioId: usuarioId);

        // Sin SET ROLE seguiríamos siendo superusuario y las políticas no se evaluarían.
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantA };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
