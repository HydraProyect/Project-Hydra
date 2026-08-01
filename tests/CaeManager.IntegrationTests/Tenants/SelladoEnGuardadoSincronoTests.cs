using CaeManager.Domain.Clientes;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Hallazgo N-15 de INFORME-AUDITORIA-2.md: <c>TenantSelladoInterceptor</c>
/// solo sobrescribía <c>SavingChangesAsync</c>. Como en el código no hay
/// ningún <c>SaveChanges()</c> síncrono, era inocuo — y por eso mismo
/// peligroso: el primero que apareciera se saltaría el sellado sin dar ningún
/// error, guardando la fila sin tenant o modificando la de otro.
///
/// Los tests usan el camino síncrono a propósito, que es justamente el que no
/// ejercita ninguna otra prueba.
/// </summary>
public class SelladoEnGuardadoSincronoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public void Un_guardado_sincrono_sella_el_tenant()
    {
        using var contexto = CrearContexto(_tenantA);

        var cliente = new Cliente("Sellada Sincrona S.L.", "B12345674", esCritico: false);
        contexto.Clientes.Add(cliente);
        contexto.SaveChanges();

        contexto.Entry(cliente).Property(nameof(Cliente.TenantId)).CurrentValue.Should().Be(_tenantA);
    }

    [Fact]
    public void Un_guardado_sincrono_sin_tenant_resuelto_falla_cerrado()
    {
        using var contexto = CrearContexto(tenantId: null);
        contexto.Clientes.Add(new Cliente("Sin Tenant S.L.", "B12345674", esCritico: false));

        var guardar = () => contexto.SaveChanges();

        guardar.Should().Throw<InvalidOperationException>().WithMessage("*sin un tenant resuelto*");
    }

    [Fact]
    public void Un_guardado_sincrono_no_puede_modificar_la_fila_de_otro_tenant()
    {
        Guid clienteId;
        using (var contextoA = CrearContexto(_tenantA))
        {
            var cliente = new Cliente("Del Tenant A S.L.", "B12345674", esCritico: false);
            contextoA.Clientes.Add(cliente);
            contextoA.SaveChanges();
            clienteId = cliente.Id;
        }

        using var contextoB = CrearContexto(_tenantB);

        // IgnoreQueryFilters para alcanzar la fila ajena a propósito: es el
        // escenario que el interceptor existe para atrapar (defensa en
        // profundidad sobre el filtro global, no sustituto).
        var ajeno = contextoB.Clientes.IgnoreQueryFilters().First(c => c.Id == clienteId);
        ajeno.Actualizar("Renombrada Por Otro Tenant S.L.", "B12345674", esCritico: false, notas: null);

        var guardar = () => contextoB.SaveChanges();

        guardar.Should().Throw<InvalidOperationException>().WithMessage("*perteneciente a otro tenant*");
    }

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
