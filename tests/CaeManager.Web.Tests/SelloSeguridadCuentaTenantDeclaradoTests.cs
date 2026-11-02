using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="SelloSeguridadCuenta"/> sobre el <c>CaeManagerDbContext</c> real y una tabla <c>AspNetUsers</c>
/// reducida. Sin RLS, la fila siempre se ve: es el caso de una conexión cuya cuenta o Tenant de origen abre
/// <c>cuentas_lectura</c> aunque el Tenant explícito sea otro, y lo único que acota la comparación es el
/// predicado de la clase.
///
/// <para>
/// Límite del instrumento: SQLite no tiene RLS ni los interceptores de producción. Esa parte se mide sobre
/// PostgreSQL en <c>SelloSeguridadCuentaBajoRuntimeTests</c>.
/// </para>
/// </summary>
public sealed class SelloSeguridadCuentaTenantDeclaradoTests : IAsyncLifetime
{
    private const string Sello = "sello-vigente";

    private readonly Guid _cuenta = Guid.NewGuid();
    private readonly Guid _tenantPropietario = Guid.NewGuid();
    private readonly SqliteConnection _conexion = new("Data Source=:memory:");
    private ServiceProvider _servicios = null!;

    public async Task InitializeAsync()
    {
        await _conexion.OpenAsync();
        var coleccion = new ServiceCollection();
        coleccion.AddScoped(_ => new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().UseSqlite(_conexion).Options,
            new EphemeralDataProtectionProvider(),
            new TenantActualAmbiental()));
        _servicios = coleccion.BuildServiceProvider();

        await using var comando = _conexion.CreateCommand();
        comando.CommandText = """
            CREATE TABLE "AspNetUsers" ("Id" TEXT NOT NULL PRIMARY KEY, "TenantId" TEXT NOT NULL, "SecurityStamp" TEXT NULL);
            INSERT INTO "AspNetUsers" ("Id", "TenantId", "SecurityStamp") VALUES ($id, $tenant, $sello);
            """;
        comando.Parameters.AddWithValue("$id", _cuenta.ToString().ToUpperInvariant());
        comando.Parameters.AddWithValue("$tenant", _tenantPropietario.ToString().ToUpperInvariant());
        comando.Parameters.AddWithValue("$sello", Sello);
        await comando.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _servicios.DisposeAsync();
        await _conexion.DisposeAsync();
    }

    private Task<bool> EsVigenteAsync(Guid tenantDeclarado, string sello) =>
        new SelloSeguridadCuenta(_servicios.GetRequiredService<IServiceScopeFactory>())
            .EsVigenteAsync(_cuenta, tenantDeclarado, sello);

    [Fact]
    public async Task En_su_Tenant_propietario_compara_el_sello()
    {
        (await EsVigenteAsync(_tenantPropietario, Sello)).Should().BeTrue();
        (await EsVigenteAsync(_tenantPropietario, "otro-sello")).Should().BeFalse();
    }

    [Fact]
    public async Task Con_otro_Tenant_declarado_no_es_vigente_aunque_la_fila_se_vea()
    {
        using var ambito = _servicios.CreateScope();
        var filaVisible = await ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>()
            .Users.AsNoTracking().AnyAsync(u => u.Id == _cuenta);
        filaVisible.Should().BeTrue("control: en este arnés nada oculta la fila");

        (await EsVigenteAsync(Guid.NewGuid(), Sello)).Should().BeFalse("el Tenant declarado forma parte de la comparación");
    }
}
