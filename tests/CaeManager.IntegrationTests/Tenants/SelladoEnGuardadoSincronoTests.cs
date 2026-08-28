using CaeManager.Domain.Empresas;
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

        var cliente = Empresa.CrearComoCliente("Sellada Sincrona S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        contexto.Empresas.Add(cliente);
        contexto.SaveChanges();

        contexto.Entry(cliente).Property(nameof(Empresa.TenantId)).CurrentValue.Should().Be(_tenantA);
    }

    [Fact]
    public void Un_guardado_sincrono_sin_tenant_resuelto_falla_cerrado()
    {
        using var contexto = CrearContexto(tenantId: null);
        contexto.Empresas.Add(Empresa.CrearComoCliente("Sin Tenant S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null));

        var guardar = () => contexto.SaveChanges();

        guardar.Should().Throw<InvalidOperationException>().WithMessage("*sin un tenant resuelto*");
    }

    [Fact]
    public void Un_guardado_sincrono_no_puede_modificar_la_fila_de_otro_tenant()
    {
        Guid clienteId;
        using (var contextoA = CrearContexto(_tenantA))
        {
            var cliente = Empresa.CrearComoCliente("Del Tenant A S.L.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
            contextoA.Empresas.Add(cliente);
            contextoA.SaveChanges();
            clienteId = cliente.Id;
        }

        using var contextoB = CrearContexto(_tenantB);

        // IgnoreQueryFilters para alcanzar la fila ajena a propósito: es el
        // escenario que el interceptor existe para atrapar (defensa en
        // profundidad sobre el filtro global, no sustituto).
        var ajeno = contextoB.Empresas.IgnoreQueryFilters().First(c => c.Id == clienteId);
        ajeno.ActualizarComoCliente("Renombrada Por Otro Tenant S.L.", "B12345674", esCritico: false, notas: null);

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
