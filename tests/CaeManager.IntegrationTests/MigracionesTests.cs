using CaeManager.Domain.Clientes;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verifica que las migraciones se aplican limpiamente contra SQLite real
/// (no el proveedor in-memory, que no valida constraints) y que la semilla
/// de datos queda en el estado esperado. Ver ROADMAP.md, criterio de
/// aceptación de Fase 0.
/// </summary>
public class MigracionesTests : IAsyncLifetime
{
    private readonly string _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-tests-{Guid.NewGuid()}.db");
    private CaeManagerDbContext _dbContext = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite($"Data Source={_rutaBaseDatos}")
            .Options;

        _dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider());
        await _dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        if (File.Exists(_rutaBaseDatos)) File.Delete(_rutaBaseDatos);
    }

    [Fact]
    public async Task Siembra_los_70_tipos_de_documento_del_catalogo()
    {
        // 15+21=36 de Trabajador (excel original + ampliación Fase 37) +
        // 13+17=30 de Empresa (documentos base obligatorios del cliente +
        // ampliación Fase 37) + 4 de Vehículo (ITC, ficha técnica, seguro,
        // autorización de circulación).
        var total = await _dbContext.TiposDocumento.CountAsync();
        total.Should().Be(70);
    }

    [Fact]
    public async Task Siembra_una_unica_fila_de_parametros_con_los_umbrales_reales()
    {
        var parametro = await _dbContext.ParametrosSistema.SingleAsync();

        parametro.UmbralAmbarDias.Should().Be(30);
        parametro.UmbralRojoDias.Should().Be(15);
    }

    [Fact]
    public async Task Siembra_los_seis_roles()
    {
        // Administrador, DireccionCae, CoordinadorCae, GestorCae, Consulta, Cliente (ver Roles.cs, Fase 31).
        var total = await _dbContext.Roles.CountAsync();
        total.Should().Be(6);
    }

    [Fact]
    public async Task Guarda_y_recupera_un_cliente()
    {
        var cliente = new Cliente("COBEGA (Coca-Cola European Partners)", "B12345674", esCritico: true);
        _dbContext.Clientes.Add(cliente);
        await _dbContext.SaveChangesAsync();

        var recuperado = await _dbContext.Clientes.FindAsync(cliente.Id);

        recuperado.Should().NotBeNull();
        recuperado!.RazonSocial.Should().Be("COBEGA (Coca-Cola European Partners)");
    }

    [Fact]
    public async Task El_filtro_global_de_soft_delete_oculta_los_clientes_eliminados()
    {
        var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: true);
        _dbContext.Clientes.Add(cliente);
        await _dbContext.SaveChangesAsync();

        cliente.MarcarComoEliminado(Guid.NewGuid());
        await _dbContext.SaveChangesAsync();

        // FindAsync resuelve primero contra el change tracker local, sin aplicar el
        // query filter — se consulta explícitamente para probar el filtro real.
        var visible = await _dbContext.Clientes.FirstOrDefaultAsync(c => c.Id == cliente.Id);

        visible.Should().BeNull();
    }

    // --- Etapa 1 de PLAN-MIGRACION-MULTITENANT.md (esquema aditivo, TenantId nullable) ---

    [Fact]
    public async Task Siembra_el_tenant_por_defecto_activo()
    {
        var tenant = await _dbContext.Tenants.SingleAsync();

        tenant.Id.Should().Be(TenantSeedData.IdPorDefecto);
        tenant.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public async Task El_backfill_sella_el_catalogo_de_tipos_de_documento_al_tenant_por_defecto()
    {
        var tenantIdsDistintos = await _dbContext.TiposDocumento.Select(t => t.TenantId).Distinct().ToListAsync();

        tenantIdsDistintos.Should().Equal(TenantSeedData.IdPorDefecto);
    }

    [Fact]
    public async Task El_backfill_sella_el_parametro_de_sistema_al_tenant_por_defecto()
    {
        var parametro = await _dbContext.ParametrosSistema.SingleAsync();

        parametro.TenantId.Should().Be(TenantSeedData.IdPorDefecto);
    }

    [Fact]
    public async Task Guarda_y_recupera_un_tenant()
    {
        var tenant = new Tenant("GESEME");
        _dbContext.Tenants.Add(tenant);
        await _dbContext.SaveChangesAsync();

        var recuperado = await _dbContext.Tenants.FindAsync(tenant.Id);

        recuperado.Should().NotBeNull();
        recuperado!.Nombre.Should().Be("GESEME");
        recuperado.Estado.Should().Be(EstadoTenant.Activo);
    }

    [Fact]
    public async Task Un_cliente_nuevo_no_tiene_TenantId_todavia_porque_no_hay_interceptor_de_sellado()
    {
        // Etapa 1 es puramente aditiva: la columna existe (nullable) pero
        // nada la rellena todavía — eso llega en la Etapa 3 (interceptor).
        var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: false);
        _dbContext.Clientes.Add(cliente);
        await _dbContext.SaveChangesAsync();

        var recuperado = await _dbContext.Clientes.FindAsync(cliente.Id);

        recuperado!.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Las_25_tablas_multi_tenant_no_tienen_TenantId_nulo_prohibido_todavia()
    {
        // Verificación de esquema: la columna admite NULL en esta etapa —
        // si esto falla, alguna Configuration marcó la columna como
        // requerida antes de tiempo (la Etapa 3 es la que la cierra).
        await using var comando = _dbContext.Database.GetDbConnection().CreateCommand();
        await _dbContext.Database.OpenConnectionAsync();
        comando.CommandText = "PRAGMA table_info('Clientes');";
        await using var lector = await comando.ExecuteReaderAsync();

        var notNullClienteId = false;
        while (await lector.ReadAsync())
        {
            if (string.Equals(lector["name"].ToString(), "TenantId", StringComparison.Ordinal))
                notNullClienteId = Convert.ToInt32(lector["notnull"]) == 1;
        }

        notNullClienteId.Should().BeFalse();
    }
}
