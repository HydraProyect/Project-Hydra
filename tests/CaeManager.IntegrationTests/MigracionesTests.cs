using CaeManager.Domain.Clientes;
using CaeManager.Infrastructure.Persistence;
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
    public async Task Siembra_los_15_tipos_de_documento_reales_del_excel()
    {
        var total = await _dbContext.TiposDocumento.CountAsync();
        total.Should().Be(15);
    }

    [Fact]
    public async Task Siembra_una_unica_fila_de_parametros_con_los_umbrales_reales()
    {
        var parametro = await _dbContext.ParametrosSistema.SingleAsync();

        parametro.UmbralAmbarDias.Should().Be(30);
        parametro.UmbralRojoDias.Should().Be(15);
    }

    [Fact]
    public async Task Siembra_los_cuatro_roles()
    {
        var total = await _dbContext.Roles.CountAsync();
        total.Should().Be(4);
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
}
