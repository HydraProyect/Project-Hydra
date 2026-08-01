using CaeManager.Domain.Common;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Guardarraíl estructural para el filtro global de tenant (P2 #27 de
/// docs/business/MATURITY_REVIEW.md, tras sustituir la lista de
/// <c>HasQueryFilter</c> enumerada a mano en <c>CaeManagerDbContext.OnModelCreating</c>
/// por un bucle de reflexión sobre <c>EntidadConTenant</c>): comprueba el
/// MODELO de EF entero, no el comportamiento de una entidad concreta como
/// <see cref="AislamientoPorAgregadoTests"/> — así detecta un olvido (o una
/// regresión del propio bucle de reflexión) para todas las entidades a la
/// vez, sin depender de que alguien recuerde añadir un test nuevo por cada
/// una. No necesita una base de datos real: el modelo de EF se construye en
/// memoria al acceder a <c>dbContext.Model</c>, sin abrir conexión.
/// </summary>
public class ModeloTenantTests
{
    [Fact]
    public void Toda_entidad_EntidadConTenant_tiene_filtro_global_de_tenant_en_el_modelo()
    {
        using var dbContext = CrearContexto();

        var tiposConTenant = dbContext.Model.GetEntityTypes()
            .Where(t => typeof(EntidadConTenant).IsAssignableFrom(t.ClrType))
            .ToList();

        tiposConTenant.Should().NotBeEmpty("si esto está vacío, el propio test dejó de poder ver el modelo real");

        // GetQueryFilter() quedó obsoleto en EF Core 10 (filtros con nombre,
        // varios por entidad) a favor de GetDeclaredQueryFilters() — Any()
        // basta aquí, no hace falta inspeccionar el filtro en sí.
        var sinFiltro = tiposConTenant
            .Where(t => !t.GetDeclaredQueryFilters().Any())
            .Select(t => t.ClrType.Name)
            .ToList();

        sinFiltro.Should().BeEmpty("toda entidad que hereda de EntidadConTenant debe tener un filtro global de tenant");
    }

    [Fact]
    public void Ninguna_entidad_de_dominio_fuera_de_EntidadConTenant_tiene_filtro_de_tenant()
    {
        using var dbContext = CrearContexto();

        // Lo contrario del test anterior: si algo que NO hereda de
        // EntidadConTenant tuviera un filtro, sería un HasQueryFilter/
        // SetQueryFilter suelto en otro archivo pisando (o compitiendo con)
        // el centralizado en OnModelCreating — justo lo que ese método dice
        // explícitamente que no debe pasar. Acotado a CaeManager.Domain para
        // no meter en esto las entidades de Identity (AspNetUsers...), que
        // son otro namespace y correctamente no llevan este filtro.
        var tiposSinTenant = dbContext.Model.GetEntityTypes()
            .Where(t => !typeof(EntidadConTenant).IsAssignableFrom(t.ClrType))
            .Where(t => t.ClrType.Namespace is not null && t.ClrType.Namespace.StartsWith("CaeManager.Domain", StringComparison.Ordinal))
            .ToList();

        var conFiltroInesperado = tiposSinTenant
            .Where(t => t.GetDeclaredQueryFilters().Any())
            .Select(t => t.ClrType.Name)
            .ToList();

        conFiltroInesperado.Should().BeEmpty("solo las entidades EntidadConTenant deben llevar filtro de tenant");
    }

    private static CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                "Host=localhost;Database=caemanager_modelo_solo_lectura",
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        // Nunca se abre conexión: solo se inspecciona dbContext.Model, que
        // EF construye en memoria a partir de OnModelCreating.
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
