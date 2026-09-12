using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// AltaGuiada.razor.cs (2026-09-12) dejó de creer los identificadores de
/// Empresa/Cliente/Centro que llegan por query string: ahora los resuelve
/// contra <c>ObtenerEmpresaPorIdQuery</c>/<c>ObtenerClientePorIdQuery</c>/
/// <c>ObtenerCentroPorIdQuery</c> antes de afirmar nada de ellos. Esa
/// resolución solo cierra el hueco si la Query de verdad no resuelve un Id
/// de OTRO tenant — una propiedad que ningún test de componente puede
/// observar, porque el arnés de bUnit sustituye la autorización por un
/// doble en vez de imponerla. Aquí se prueba contra Postgres real, con el
/// mismo patrón de dos <see cref="CaeManagerDbContext"/> independientes
/// sobre la misma base física que <c>AislamientoMultiTenantTests</c>.
///
/// <para>
/// El alcance de cartera (<see cref="AlcanceDatosServiceFalso"/>) se deja
/// SIN restricción a propósito en los tres tests: así el único mecanismo que
/// puede estar impidiendo la resolución es el filtro de tenant, no una
/// cartera que también fallaría en negar el acceso. Aislar exactamente esa
/// capa es la razón de que este fichero exista además de
/// <c>AlcancePorIdTests</c>, que prueba la cartera dentro de un mismo
/// tenant.
/// </para>
/// </summary>
public class ObtenerPorIdMultiTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    private Guid _empresaDeAId;
    private Guid _clienteDeAId;
    private Guid _centroDeAId;

    public async Task InitializeAsync()
    {
        await using var contextoA = CrearContexto(_tenantA);
        await contextoA.Database.MigrateAsync();

        var empresa = new Empresa("Ibertec S.A.");
        var cliente = Empresa.CrearComoCliente("Cadena Industrial Iberia S.A.", "B12345674", true, null, null);
        contextoA.Empresas.AddRange(empresa, cliente);
        await contextoA.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Planta Sevilla");
        contextoA.Centros.Add(centro);
        await contextoA.SaveChangesAsync();

        _empresaDeAId = empresa.Id;
        _clienteDeAId = cliente.Id;
        _centroDeAId = centro.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Una_empresa_del_tenant_A_no_resuelve_para_el_tenant_B_aunque_su_alcance_sea_total()
    {
        await using var contextoB = CrearContexto(_tenantB);
        var handler = new ObtenerEmpresaPorIdQueryHandler(contextoB, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerEmpresaPorIdQuery(_empresaDeAId), CancellationToken.None);

        resultado.Should().BeNull(
            "el filtro de tenant tiene que negar la fila antes de que la cartera (aquí sin restricción) llegue a decidir nada");
    }

    [Fact]
    public async Task Un_cliente_del_tenant_A_no_resuelve_para_el_tenant_B_aunque_su_alcance_sea_total()
    {
        await using var contextoB = CrearContexto(_tenantB);
        var handler = new ObtenerClientePorIdQueryHandler(contextoB, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerClientePorIdQuery(_clienteDeAId), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Un_centro_del_tenant_A_no_resuelve_para_el_tenant_B_aunque_su_alcance_sea_total()
    {
        await using var contextoB = CrearContexto(_tenantB);
        var handler = new ObtenerCentroPorIdQueryHandler(contextoB, contextoB, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerCentroPorIdQuery(_centroDeAId), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Los_tres_identificadores_siguen_resolviendo_para_su_propio_tenant()
    {
        await using var contextoA = CrearContexto(_tenantA);
        var alcance = new AlcanceDatosServiceFalso();

        (await new ObtenerEmpresaPorIdQueryHandler(contextoA, alcance)
            .Handle(new ObtenerEmpresaPorIdQuery(_empresaDeAId), CancellationToken.None)).Should().NotBeNull(
            "el propio tenant sigue viendo sus datos — la prueba anterior aísla el tenant, no rompe la resolución");

        (await new ObtenerClientePorIdQueryHandler(contextoA, alcance)
            .Handle(new ObtenerClientePorIdQuery(_clienteDeAId), CancellationToken.None)).Should().NotBeNull();

        (await new ObtenerCentroPorIdQueryHandler(contextoA, contextoA, alcance)
            .Handle(new ObtenerCentroPorIdQuery(_centroDeAId), CancellationToken.None)).Should().NotBeNull();
    }
}
