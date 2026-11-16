using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Archivos;

/// <summary>
/// La RLS <c>aislamiento_tenant</c> y los grants de la migración <c>AnadeArchivosPendientesDeReferencia</c> (issue #2123),
/// medidos como <c>cae_app_runtime</c>: el outbox es del Tenant propietario, un Tenant no ve, no escribe ni borra las
/// filas de otro, el runtime no las cambia y Soporte TALVEG no las lee.
/// </summary>
public class ArchivosPendientesDeReferenciaRlsTests : IAsyncLifetime
{
    private const string PermisoDenegado = "42501";

    private readonly Guid _usuario = Guid.NewGuid();
    private ArnesDeArranqueRuntime _arnes = null!;
    private Guid _tenantA;
    private Guid _tenantB;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        await using var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
        var a = new Tenant("Tenant A");
        var b = new Tenant("Tenant B");
        propietario.Tenants.AddRange(a, b);
        await propietario.SaveChangesAsync();
        (_tenantA, _tenantB) = (a.Id, b.Id);
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Un_Tenant_no_ve_ni_borra_las_filas_de_otro_y_no_escribe_en_su_nombre()
    {
        await using (var a = await AbrirComoRuntimeAsync(_tenantA))
            (await EjecutarAsync(a, Insertar(_tenantA, "a/huerfano.pdf"))).Should().BeNull("control positivo: el Tenant A apunta lo suyo");

        (await ContarAsync(_tenantA)).Should().Be(1, "control positivo de la RLS");
        (await ContarAsync(_tenantB)).Should().Be(0, "el Tenant B no ve las filas del Tenant A");

        await using (var b = await AbrirComoRuntimeAsync(_tenantB))
        {
            await using var borrar = b.CreateCommand();
            borrar.CommandText = """DELETE FROM "ArchivosPendientesDeReferencia";""";
            (await borrar.ExecuteNonQueryAsync()).Should().Be(0, "USING de aislamiento_tenant: el Tenant B no borra filas del Tenant A");
            (await EjecutarAsync(b, Insertar(_tenantA, "a/colada.pdf"))).Should().Be(PermisoDenegado,
                "WITH CHECK de aislamiento_tenant: el Tenant B no escribe filas del Tenant A");
        }

        (await ContarAsync(_tenantA)).Should().Be(1, "la fila del Tenant A sigue intacta");
    }

    [Fact]
    public async Task El_runtime_apunta_lee_y_borra_pero_no_cambia_filas()
    {
        await using var a = await AbrirComoRuntimeAsync(_tenantA);
        (await EjecutarAsync(a, Insertar(_tenantA, "a/x.pdf"))).Should().BeNull();
        (await EjecutarAsync(a, """UPDATE "ArchivosPendientesDeReferencia" SET "GuardadoEnUtc" = now() - interval '2 days';"""))
            .Should().Be(PermisoDenegado, "nadie adelanta el barrido de una fila: el runtime no tiene UPDATE");
        (await EjecutarAsync(a, """DELETE FROM "ArchivosPendientesDeReferencia";""")).Should().BeNull("control positivo: el runtime borra las suyas");
        (await ContarAsync(_tenantA)).Should().Be(0);
    }

    [Fact]
    public async Task Soporte_TALVEG_no_lee_el_outbox()
    {
        await using var conexion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await conexion.OpenAsync();
        (await EjecutarAsync(conexion, "SET ROLE cae_app_soporte;")).Should().BeNull();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: _tenantA, usuarioId: _usuario);
        (await EjecutarAsync(conexion, """SELECT count(*) FROM "ArchivosPendientesDeReferencia";"""))
            .Should().Be(PermisoDenegado, "la migración no concede nada a cae_app_soporte");
    }

    private static string Insertar(Guid tenant, string clave) =>
        $"""INSERT INTO "ArchivosPendientesDeReferencia" ("Id", "Clave", "GuardadoEnUtc", "TenantId") VALUES ('{Guid.NewGuid()}', '{clave}', now(), '{tenant}');""";

    private static async Task<string?> EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        return await Record.ExceptionAsync(() => comando.ExecuteNonQueryAsync()) switch
        {
            null => null,
            PostgresException e => e.SqlState,
            var otra => throw otra,
        };
    }

    private async Task<long> ContarAsync(Guid tenant)
    {
        await using var conexion = await AbrirComoRuntimeAsync(tenant);
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = """SELECT count(*) FROM "ArchivosPendientesDeReferencia";""";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync(Guid tenant)
    {
        var conexion = new NpgsqlConnection(BaseDatosPostgresDePruebas.CadenaComoRuntime(_arnes.CadenaPropietario));
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, usuarioId: _usuario);
        return conexion;
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
