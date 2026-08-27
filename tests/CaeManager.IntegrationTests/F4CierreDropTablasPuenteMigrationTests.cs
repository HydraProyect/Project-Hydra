using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Cierre de F4 — el <c>DROP</c> de las tres tablas puente legacy y, sobre
/// todo, <b>la propiedad del <c>Down</c> que ningún otro instrumento puede
/// observar</b>: al revertir, las tablas vuelven CON su aislamiento por
/// tenant.
///
/// <para>
/// Por qué existe este test y no basta con leer el código: las políticas RLS
/// no las genera EF (viven en SQL crudo desde
/// <c>20260801120000_HabilitarRlsPostgres</c>), así que un <c>Down</c>
/// generado automáticamente recrea las tablas <em>desnudas</em>. Un rollback
/// dejaría entonces tres tablas con <c>TenantId</c> legibles entre tenants —
/// un agujero de aislamiento abierto justo en el momento de mayor estrés
/// operativo, y silencioso: la reversión "funciona". El bloque que lo evita
/// se falsó por mutación (retirarlo deja este test en rojo nombrando la
/// tabla sin política).
/// </para>
/// </summary>
public class F4CierreDropTablasPuenteMigrationTests : IAsyncLifetime
{
    private const string MigracionAntesDelCierre = "AgregarRelacionEmpresarial";
    private const string MigracionDelCierre = "F4CierreDropTablasPuente";

    private static readonly string[] TablasPuente =
        ["EmpresasClientes", "SubcontratasClientes", "SubcontratasEmpresas"];

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Up_retira_fisicamente_las_tres_tablas_puente()
    {
        var existentes = await TablasExistentesAsync();

        existentes.Should().BeEmpty(
            "el cierre de F4 retira físicamente las tres tablas puente legacy; los datos quedaron " +
            "preservados en el artefacto de migración citado en el doc-comment de la migración");
    }

    [Fact]
    public async Task El_Down_devuelve_las_tablas_CON_su_politica_de_aislamiento()
    {
        await using (var contexto = CrearContexto())
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionAntesDelCierre);
        }

        (await TablasExistentesAsync()).Should().BeEquivalentTo(TablasPuente,
            "revertir el cierre debe devolver las tres tablas");

        foreach (var tabla in TablasPuente)
        {
            (await TienePoliticaDeAislamientoAsync(tabla)).Should().BeTrue(
                $"tras revertir, «{tabla}» vuelve a contener datos de varios tenants: sin la política " +
                "aislamiento_tenant quedaría legible entre tenants");
            (await TieneRlsForzadaAsync(tabla)).Should().BeTrue(
                $"«{tabla}» necesita FORCE ROW LEVEL SECURITY: sin él la política no restringe al " +
                "propietario de la tabla, que es el rol con el que migran todos los entornos");
        }
    }

    private async Task<List<string>> TablasExistentesAsync()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            """
            SELECT table_name FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name = ANY(@tablas)
            ORDER BY table_name;
            """, conexion);
        comando.Parameters.AddWithValue("tablas", TablasPuente);

        var encontradas = new List<string>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            encontradas.Add(lector.GetString(0));

        return encontradas;
    }

    private async Task<bool> TienePoliticaDeAislamientoAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT count(*) FROM pg_policies WHERE tablename = @tabla AND policyname = 'aislamiento_tenant';",
            conexion);
        comando.Parameters.AddWithValue("tabla", tabla);

        return Convert.ToInt32(await comando.ExecuteScalarAsync()) == 1;
    }

    private async Task<bool> TieneRlsForzadaAsync(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            "SELECT relforcerowsecurity FROM pg_class WHERE relname = @tabla;", conexion);
        comando.Parameters.AddWithValue("tabla", tabla);

        return await comando.ExecuteScalarAsync() is true;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
