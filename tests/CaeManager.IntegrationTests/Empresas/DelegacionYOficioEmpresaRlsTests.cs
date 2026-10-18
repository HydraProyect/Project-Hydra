using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.IntegrationTests.Migraciones;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// <c>DelegacionesEmpresa</c> y <c>OficiosEmpresa</c> bajo <c>cae_app_runtime</c>, el rol de producción: otro Tenant no
/// ve nada, el <c>WITH CHECK</c> no deja regalar una fila a otro Tenant, y la FK compuesta
/// <c>(TenantId, EmpresaId, …)</c> de Trabajadores no deja asignar el catálogo de otra Empresa. Además, la migración de
/// datos que convierte cada Puesto distinto de cada Empresa en un Oficio.
/// </summary>
public class DelegacionYOficioEmpresaRlsTests : IAsyncLifetime
{
    private const string MigracionAnterior = "20261018182902_AnadeContactoEmergenciaATrabajador";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private Guid _empresaA;
    private Guid _otraEmpresaA;
    private Guid _subcontrataA;
    private Guid _empresaB;

    public static TheoryData<string> Tablas => ["DelegacionesEmpresa", "OficiosEmpresa"];

    public async Task InitializeAsync()
    {
        // Esquema anterior a la migración, con Puestos sembrados, para que la migración de datos tenga qué convertir.
        await using (var real = CrearContexto(_tenantA))
            await real.GetService<IMigrator>().MigrateAsync(MigracionAnterior);

        await using (var anterior = CrearContextoAnterior(_tenantA))
        {
            var empresa = new Empresa("Montajes A S.L.", "B12345674");
            var otra = new Empresa("Otra A S.L.", "B87654323");
            var subcontrata = new Empresa("Subcontrata A S.L.", "B10380186");
            anterior.Empresas.AddRange(empresa, otra, subcontrata);
            await anterior.SaveChangesAsync();
            anterior.Trabajadores.AddRange(
                Trabajador.DeEmpresa(empresa.Id, "Ana", "Uno", "12345678Z", puesto: "Soldador"),
                Trabajador.DeEmpresa(empresa.Id, "Luis", "Dos", "77189989B", puesto: "  SOLDADOR  "),
                Trabajador.DeEmpresa(empresa.Id, "Eva", "Tres", "00000000T", puesto: "Pintor"),
                Trabajador.DeSubcontrata(subcontrata.Id, "Sara", "Cuatro", "11111111H"));
            await anterior.SaveChangesAsync();
            (_empresaA, _otraEmpresaA, _subcontrataA) = (empresa.Id, otra.Id, subcontrata.Id);
        }

        await using (var real = CrearContexto(_tenantA))
            await real.Database.MigrateAsync();

        await using (var a = CrearContexto(_tenantA))
        {
            a.DelegacionesEmpresa.Add(new DelegacionEmpresa(_empresaA, "Sevilla"));
            await a.SaveChangesAsync();
        }

        await using var b = CrearContexto(_tenantB);
        var empresaB = new Empresa("Montajes B S.L.", "B10380194");
        b.Empresas.Add(empresaB);
        await b.SaveChangesAsync();
        b.DelegacionesEmpresa.Add(new DelegacionEmpresa(empresaB.Id, "Bilbao"));
        b.OficiosEmpresa.Add(new OficioEmpresa(empresaB.Id, "Electricista"));
        await b.SaveChangesAsync();
        _empresaB = empresaB.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task La_migracion_crea_un_oficio_por_puesto_distinto_de_cada_empresa_y_lo_asigna()
    {
        await using var db = CrearContexto(_tenantA);

        var oficios = await db.OficiosEmpresa.Where(o => o.EmpresaId == _empresaA).Select(o => o.Nombre).ToListAsync();
        oficios.Should().HaveCount(2, "«Soldador» y «  SOLDADOR  » son el mismo oficio").And.Contain("Pintor");

        var trabajadores = await db.Trabajadores.ToListAsync();
        trabajadores.Where(t => t.EmpresaId == _empresaA).Should().OnlyContain(t => t.OficioEmpresaId != null);
        trabajadores.Single(t => t.Dni == "77189989B").Puesto.Should().Be("SOLDADOR", "la migración no toca el Puesto (el dominio ya lo guardó sin espacios en los extremos)");
        trabajadores.Single(t => t.SubcontrataId == _subcontrataA).OficioEmpresaId.Should().BeNull();
        (await db.OficiosEmpresa.CountAsync(o => o.EmpresaId == _otraEmpresaA)).Should().Be(0);
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public async Task Sin_tenant_de_sesion_no_se_ve_nada_y_cada_tenant_solo_ve_lo_suyo(string tabla)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        (await ContarAsync(conexion, tabla)).Should().Be(0);

        await FijarTenantAsync(conexion, _tenantB);
        (await ContarAsync(conexion, tabla)).Should().Be(1, "el Tenant B solo ve su fila, nunca las del Tenant A");
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public async Task El_with_check_rechaza_mover_una_fila_a_otro_tenant(string tabla)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantB);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE \"{tabla}\" SET \"TenantId\" = @destino;";
        comando.Parameters.AddWithValue("destino", _tenantA);

        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // Control positivo: lo que se rechaza es el cambio de dueño, no la escritura.
        comando.CommandText = $"UPDATE \"{tabla}\" SET \"Nombre\" = \"Nombre\" || ' (editada)';";
        comando.Parameters.Clear();
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_insert_en_otro_tenant_lo_rechaza_el_with_check()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantB);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO \"OficiosEmpresa\" (\"Id\", \"TenantId\", \"EmpresaId\", \"Nombre\", \"Version\", \"CreadoEnUtc\", \"EstaEliminado\") " +
            "VALUES (gen_random_uuid(), @ajeno, @empresa, 'Sembrado en ajeno', gen_random_uuid(), now(), false);";
        comando.Parameters.AddWithValue("ajeno", _tenantA);
        comando.Parameters.AddWithValue("empresa", _empresaA);

        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Un_trabajador_no_puede_tener_la_delegacion_de_otra_empresa_ni_ser_de_subcontrata()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantA);

        await using (var otra = conexion.CreateCommand())
        {
            otra.CommandText =
                "INSERT INTO \"DelegacionesEmpresa\" (\"Id\", \"TenantId\", \"EmpresaId\", \"Nombre\", \"Version\", \"CreadoEnUtc\", \"EstaEliminado\") " +
                "VALUES (gen_random_uuid(), @tenant, @otra, 'De la otra', gen_random_uuid(), now(), false);";
            otra.Parameters.AddWithValue("tenant", _tenantA);
            otra.Parameters.AddWithValue("otra", _otraEmpresaA);
            await otra.ExecuteNonQueryAsync();
        }

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE \"Trabajadores\" SET \"DelegacionEmpresaId\" = (SELECT \"Id\" FROM \"DelegacionesEmpresa\" WHERE \"EmpresaId\" = @otra) " +
            "WHERE \"EmpresaId\" = @empresa;";
        comando.Parameters.AddWithValue("otra", _otraEmpresaA);
        comando.Parameters.AddWithValue("empresa", _empresaA);
        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        comando.CommandText =
            "UPDATE \"Trabajadores\" SET \"OficioEmpresaId\" = (SELECT \"Id\" FROM \"OficiosEmpresa\" LIMIT 1) " +
            "WHERE \"SubcontrataId\" = @subcontrata;";
        comando.Parameters.Clear();
        comando.Parameters.AddWithValue("subcontrata", _subcontrataA);
        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task El_repositorio_de_otro_tenant_no_lee_la_delegacion()
    {
        await using var a = CrearContexto(_tenantA);
        var delegacionA = await a.DelegacionesEmpresa.SingleAsync(d => d.EmpresaId == _empresaA);

        await using var b = CrearContexto(_tenantB);
        var repositorio = new Infrastructure.Persistence.Repositories.CatalogoTrabajadoresEmpresaRepository(b);
        (await repositorio.ObtenerDelegacionEmpresaAsync(delegacionA.Id)).Should().BeNull();
        (await repositorio.ListarDelegacionesEmpresaAsync(_empresaA)).Should().BeEmpty();
        (await repositorio.ListarOficiosAsync(_empresaB)).Should().ContainSingle();
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();
        return conexion;
    }

    private static async Task FijarTenantAsync(NpgsqlConnection conexion, Guid tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantId.ToString());
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private DbContextOptions<CaeManagerDbContext> Opciones(TenantActualAmbiental tenant) =>
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, p => p.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant), new ConcurrenciaOptimistaInterceptor())
            .Options;

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        return new(Opciones(tenant), new EphemeralDataProtectionProvider(), tenant);
    }

    private ContextoAnteriorALaMarcaDePrincipal CrearContextoAnterior(Guid tenantId)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        return new(Opciones(tenant), new EphemeralDataProtectionProvider(), tenant);
    }
}
