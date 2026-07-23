using CaeManager.Domain.Alertas;
using CaeManager.Domain.Clientes;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// La validación que más importa de la Etapa 3 (cierre): dos tenants sobre
/// la misma base de datos física nunca se ven entre sí, en ninguna
/// dirección. Cada test crea dos <see cref="CaeManagerDbContext"/>
/// independientes apuntando al mismo archivo SQLite, cada uno con su propio
/// <see cref="TenantActualAmbiental"/> — exactamente el escenario real de
/// dos tenants concurrentes sobre la misma instalación (ver
/// docs/MULTITENANCY.md).
/// </summary>
public class AislamientoMultiTenantTests : IAsyncLifetime
{
    private readonly string _rutaBaseDatos = Path.Combine(Path.GetTempPath(), $"caemanager-aislamiento-{Guid.NewGuid()}.db");
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public Task DisposeAsync()
    {
        if (File.Exists(_rutaBaseDatos)) File.Delete(_rutaBaseDatos);
        return Task.CompletedTask;
    }

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite($"Data Source={_rutaBaseDatos}")
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Un_cliente_creado_por_el_tenant_A_es_invisible_para_el_tenant_B()
    {
        Guid clienteId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: false);
            contextoA.Clientes.Add(cliente);
            await contextoA.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var visibleParaB = await contextoB.Clientes.FirstOrDefaultAsync(c => c.Id == clienteId);
        visibleParaB.Should().BeNull();

        await using var contextoAOtraVez = CrearContexto(_tenantA);
        var visibleParaA = await contextoAOtraVez.Clientes.FirstOrDefaultAsync(c => c.Id == clienteId);
        visibleParaA.Should().NotBeNull();
    }

    [Fact]
    public async Task Una_alerta_creada_por_el_tenant_A_es_invisible_para_el_tenant_B()
    {
        // Cubre también el caso de entidad EntidadConTenant-directa (sin soft
        // delete), no solo las que extienden EntidadBase como Cliente.
        Guid alertaId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var alerta = new Alerta(Guid.NewGuid(), NivelAlerta.Urgente);
            contextoA.Alertas.Add(alerta);
            await contextoA.SaveChangesAsync();
            alertaId = alerta.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var visibleParaB = await contextoB.Alertas.FirstOrDefaultAsync(a => a.Id == alertaId);
        visibleParaB.Should().BeNull();
    }

    [Fact]
    public async Task El_interceptor_sella_TenantId_del_tenant_actual_sin_que_el_codigo_lo_asigne()
    {
        await using var contextoA = CrearContexto(_tenantA);
        var cliente = new Cliente("KHS S.A.", "B12345674", esCritico: false);
        contextoA.Clientes.Add(cliente);
        await contextoA.SaveChangesAsync();

        cliente.TenantId.Should().Be(_tenantA);
    }

    [Fact]
    public async Task No_se_puede_crear_una_entidad_sin_tenant_resuelto()
    {
        await using var contextoSinTenant = CrearContexto(tenantId: null);
        var cliente = new Cliente("Sin tenant", "B12345674", esCritico: false);
        contextoSinTenant.Clientes.Add(cliente);

        var accion = async () => await contextoSinTenant.SaveChangesAsync();

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_se_puede_modificar_una_entidad_perteneciente_a_otro_tenant()
    {
        Guid clienteId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: false);
            contextoA.Clientes.Add(cliente);
            await contextoA.SaveChangesAsync();
            clienteId = cliente.Id;
        }

        // El filtro global ya impide que el tenant B cargue esta fila por
        // una consulta normal — este test simula el caso residual que el
        // interceptor cubre como defensa en profundidad: una entidad cargada
        // saltándose el filtro (IgnoreQueryFilters justificado y revisado,
        // ver docs/MULTITENANCY.md § 4.2) y modificada después.
        await using var contextoB = CrearContexto(_tenantB);
        var clienteDeOtroTenant = await contextoB.Clientes
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == clienteId);

        clienteDeOtroTenant.Actualizar("Nombre modificado por otro tenant", "B12345674", esCritico: true, notas: null);

        var accion = async () => await contextoB.SaveChangesAsync();

        await accion.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task El_mismo_cif_puede_existir_en_dos_tenants_distintos_pero_no_duplicado_en_el_mismo_tenant()
    {
        const string cifCompartido = "B12345674";

        await using (var contextoA = CrearContexto(_tenantA))
        {
            contextoA.Clientes.Add(new Cliente("Empresa en tenant A", cifCompartido, esCritico: false));
            await contextoA.SaveChangesAsync();
        }

        // Mismo CIF, tenant distinto — debe permitirse (caso de negocio real,
        // ver docs/MULTITENANCY.md § 5).
        await using (var contextoB = CrearContexto(_tenantB))
        {
            contextoB.Clientes.Add(new Cliente("La misma empresa, vista por otro tenant", cifCompartido, esCritico: false));
            var guardarEnB = async () => await contextoB.SaveChangesAsync();
            await guardarEnB.Should().NotThrowAsync();
        }

        // Mismo CIF, mismo tenant — debe rechazarse por el índice único compuesto.
        await using (var contextoADuplicado = CrearContexto(_tenantA))
        {
            contextoADuplicado.Clientes.Add(new Cliente("Duplicado dentro del mismo tenant", cifCompartido, esCritico: false));
            var guardarDuplicado = async () => await contextoADuplicado.SaveChangesAsync();
            await guardarDuplicado.Should().ThrowAsync<DbUpdateException>();
        }
    }
}
