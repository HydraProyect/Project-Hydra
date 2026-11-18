using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.ControlAcceso;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.ControlAcceso;

/// <summary>
/// AF-01: la tabla <c>UsuariosControlAccesoCentro</c> (los Centros de portería de cada persona con el rol «Control de acceso»)
/// bajo <c>cae_app_runtime</c> con contexto firmado. X e Y son dos Tenants TCE. Lo que prueba la base: RLS habilitada y forzada
/// con la política de aislamiento; el runtime inserta, lee y borra pero no reescribe; Soporte solo lee; un Tenant no ve ni
/// borra las filas de otro y no puede escribir con el Tenant de otro; la clave foránea compuesta impide asignar un Centro de
/// otro Tenant; y no hay dos filas iguales.
/// </summary>
public class UsuariosControlAccesoCentroBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _x = new("Tenant TCE X");
    private readonly Tenant _y = new("Tenant TCE Y");
    private readonly Guid _usuarioX = Guid.NewGuid();
    private readonly Guid _usuarioY = Guid.NewGuid();
    private readonly Guid _portero = Guid.NewGuid();

    private Guid _centroX1;
    private Guid _centroX2;
    private Guid _centroY;

    public async Task InitializeAsync()
    {
        await using (var contexto = Contexto(_x.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_x, _y);
            await contexto.SaveChangesAsync();
        }

        _centroX1 = await CrearCentroAsync(_x.Id, "B10380186", "Planta 1 de X");
        _centroX2 = await CrearCentroAsync(_x.Id, "B10380194", "Planta 2 de X");
        _centroY = await CrearCentroAsync(_y.Id, "B10380186", "Planta de Y");
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    private async Task<Guid> CrearCentroAsync(Guid tenantId, string cif, string nombre)
    {
        await using var contexto = Contexto(tenantId);
        var cliente = Empresa.CrearComoCliente($"Cliente de {nombre}", cif, false, null, null);
        contexto.Empresas.Add(cliente);
        var centro = new Centro(cliente.Id, cliente.Id, nombre);
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();
        return centro.Id;
    }

    [Fact]
    public async Task La_tabla_tiene_RLS_forzada_con_la_politica_de_aislamiento()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """
            SELECT c.relrowsecurity, c.relforcerowsecurity,
                   (SELECT array_agg(p.polname::text ORDER BY p.polname) FROM pg_policy p WHERE p.polrelid = c.oid)
            FROM pg_class c WHERE c.oid = 'public."UsuariosControlAccesoCentro"'::regclass;
            """, propietario);
        await using var lector = await orden.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();

        lector.GetBoolean(0).Should().BeTrue("ENABLE ROW LEVEL SECURITY");
        lector.GetBoolean(1).Should().BeTrue("FORCE ROW LEVEL SECURITY");
        lector.GetFieldValue<string[]>(2).Should().Equal("aislamiento_tenant");
    }

    [Fact]
    public async Task El_runtime_lee_inserta_y_borra_pero_no_reescribe_y_Soporte_solo_lee()
    {
        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();

        async Task<bool> Tabla(string rol, string privilegio)
        {
            await using var orden = new NpgsqlCommand(
                "SELECT has_table_privilege(@rol, 'public.\"UsuariosControlAccesoCentro\"', @privilegio)", propietario);
            orden.Parameters.AddWithValue("rol", rol);
            orden.Parameters.AddWithValue("privilegio", privilegio);
            return (bool)(await orden.ExecuteScalarAsync())!;
        }

        foreach (var privilegio in new[] { "SELECT", "INSERT", "DELETE" })
            (await Tabla("cae_app_runtime", privilegio)).Should().BeTrue($"runtime: {privilegio}");
        (await Tabla("cae_app_runtime", "UPDATE")).Should().BeFalse("asignar es insertar y retirar es borrar: no hay UPDATE");
        (await Tabla("cae_app_runtime", "TRUNCATE")).Should().BeFalse();

        (await Tabla("cae_app_soporte", "SELECT")).Should().BeTrue();
        foreach (var privilegio in new[] { "INSERT", "UPDATE", "DELETE" })
            (await Tabla("cae_app_soporte", privilegio)).Should().BeFalse($"Soporte: {privilegio}");
        foreach (var privilegio in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
            (await Tabla("cae_app_aprovisionamiento", privilegio)).Should().BeFalse($"aprovisionamiento: {privilegio}");
    }

    [Fact]
    public async Task Cada_Tenant_ve_solo_sus_filas_y_no_borra_las_de_otro()
    {
        await AsignarAsync(_x.Id, _usuarioX, _portero, _centroX1);
        await AsignarAsync(_y.Id, _usuarioY, _portero, _centroY);

        (await CentrosDeAsync(_x.Id, _usuarioX, _portero)).Should().Equal(_centroX1);
        (await CentrosDeAsync(_y.Id, _usuarioY, _portero)).Should().Equal(_centroY);

        // Bajo el Tenant Y, DELETE no alcanza la fila de X: 0 filas, sin error.
        await using (var contexto = ContextoRuntime(_y.Id, _usuarioY))
            (await contexto.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"UsuariosControlAccesoCentro\" WHERE \"CentroId\" = {0}", _centroX1)).Should().Be(0);

        (await CentrosDeAsync(_x.Id, _usuarioX, _portero)).Should().Equal(_centroX1, "la fila de X sigue ahí");
    }

    [Fact]
    public async Task El_runtime_no_escribe_con_el_Tenant_de_otro()
    {
        await using var contexto = ContextoRuntime(_x.Id, _usuarioX);

        var error = (await FluentActions.Awaiting(() => contexto.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"UsuariosControlAccesoCentro\" (\"Id\", \"TenantId\", \"UsuarioId\", \"CentroId\") VALUES ({0}, {1}, {2}, {3})",
                    Guid.NewGuid(), _y.Id, _portero, _centroY)).Should().ThrowAsync<PostgresException>())
            .Which;

        error.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la política rechaza la fila con el Tenant de otro");
    }

    [Fact]
    public async Task La_clave_foranea_compuesta_impide_asignar_un_Centro_de_otro_Tenant()
    {
        await using var contexto = ContextoRuntime(_x.Id, _usuarioX);
        contexto.UsuariosControlAccesoCentro.Add(new UsuarioControlAccesoCentro(_portero, _centroY));

        var error = (await FluentActions.Awaiting(() => contexto.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>().Subject;

        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task No_hay_dos_filas_iguales_para_la_misma_persona_y_el_mismo_Centro()
    {
        await AsignarAsync(_x.Id, _usuarioX, _portero, _centroX1);

        await using var contexto = ContextoRuntime(_x.Id, _usuarioX);
        contexto.UsuariosControlAccesoCentro.Add(new UsuarioControlAccesoCentro(_portero, _centroX1));

        var error = (await FluentActions.Awaiting(() => contexto.SaveChangesAsync()).Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>().Subject;

        (error.SqlState, error.ConstraintName).Should().Be(
            (PostgresErrorCodes.UniqueViolation, "IX_UsuariosControlAccesoCentro_Unico"));
    }

    [Fact]
    public async Task En_otro_Centro_del_mismo_Tenant_la_persona_no_tiene_asignacion()
    {
        await AsignarAsync(_x.Id, _usuarioX, _portero, _centroX1);

        (await CentrosDeAsync(_x.Id, _usuarioX, _portero)).Should().NotContain(_centroX2);
    }

    [Fact]
    public async Task Un_Centro_con_asignaciones_no_se_borra_en_silencio()
    {
        await AsignarAsync(_x.Id, _usuarioX, _portero, _centroX1);

        await using var propietario = new NpgsqlConnection(_cadena);
        await propietario.OpenAsync();
        await using var orden = new NpgsqlCommand("DELETE FROM \"Centros\" WHERE \"Id\" = @id", propietario);
        orden.Parameters.AddWithValue("id", _centroX1);

        (await FluentActions.Awaiting(() => orden.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation, "la FK es Restrict");
    }

    private async Task AsignarAsync(Guid tenantId, Guid actor, Guid portero, Guid centroId)
    {
        await using var contexto = ContextoRuntime(tenantId, actor);
        contexto.UsuariosControlAccesoCentro.Add(new UsuarioControlAccesoCentro(portero, centroId));
        await contexto.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<Guid>> CentrosDeAsync(Guid tenantId, Guid actor, Guid portero)
    {
        await using var contexto = ContextoRuntime(tenantId, actor);
        return await contexto.UsuariosControlAccesoCentro.Where(a => a.UsuarioId == portero).Select(a => a.CentroId).ToListAsync();
    }

    private CaeManagerDbContext ContextoRuntime(Guid tenantId, Guid usuario) => Contexto(tenantId, usuario);

    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        var actual = usuario is { } id ? new CurrentUserServiceFalso(id, Roles.GestorCae, tenantOrigenId: tenantId) : null;
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            actual is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (actual is not null)
            opciones.AddInterceptors(
                new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(), actual, BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : CaeManager.Application.Common.IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
