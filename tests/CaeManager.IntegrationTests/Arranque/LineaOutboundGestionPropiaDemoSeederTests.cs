using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>La línea Outbound de gestión propia (P) de la siembra de Development la lleva el Tenant de demostración que gestiona su
/// CAE con su propio personal, y ningún otro; una segunda pasada no escribe nada.</b> Corre sobre PostgreSQL real con el
/// <see cref="CaeManagerDbContext"/> como cae_app_runtime (RLS efectiva), y las siembras que crean Tenants de demostración de
/// las que decide la tabla de la PR (cobertura: Alfa, Beta y su Operador CAE externo; Inbound: el Tenant TCE y tres TEC) corren
/// antes que la línea, en el orden de Program.cs. Cada recuento se lee dentro del ámbito de su Tenant, y se recorren
/// <b>todos</b> los Tenants de la base, así que una fila sembrada en el Tenant equivocado se vería.
/// </summary>
public sealed class LineaOutboundGestionPropiaDemoSeederFixture : IAsyncLifetime
{
    internal ArnesDeArranqueRuntime Arnes { get; private set; } = null!;
    internal IConfiguration Configuracion { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        Configuracion = new ConfigurationBuilder()
            .AddConfiguration(Arnes.Servicios.GetRequiredService<IConfiguration>())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CoberturaPantallasSeeder.ClaveConfiguracion] = "true",
                [InboundDemoSeeder.ClaveConfiguracion] = "true"
            })
            .Build();

        using var ambito = Arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        await CoberturaPantallasSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await InboundDemoSeeder.SeedAsync(
            sp.GetRequiredService<CaeManagerDbContext>(), sp.GetRequiredService<UserManager<ApplicationUser>>(),
            sp.GetRequiredService<IUserStore<ApplicationUser>>(), Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await SembrarLineasAsync();
    }

    /// <summary>Como en Program.cs: las dos líneas del Producto Contratado, con el contexto de bootstrap y al final.</summary>
    internal async Task SembrarLineasAsync()
    {
        using var ambito = Arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        await LineaInboundDemoSeeder.SeedAsync(bootstrap, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, Configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
    }

    public Task DisposeAsync() => Arnes.DisposeAsync().AsTask();
}

public class LineaOutboundGestionPropiaDemoSeederTests(LineaOutboundGestionPropiaDemoSeederFixture fixture)
    : IClassFixture<LineaOutboundGestionPropiaDemoSeederFixture>
{
    [Fact]
    public async Task El_Tenant_Beta_tiene_la_linea_P_vigente_con_los_datos_de_demostracion()
    {
        var lineas = await LineasDeAsync(CoberturaPantallasSeeder.NombreTenantBeta);

        var linea = lineas.Should().ContainSingle().Subject;
        linea.Linea.Should().Be(LineaProductoContratado.OutboundGestionPropia);
        linea.Estado.Should().Be(EstadoProductoContratado.Vigente);
        linea.VigenteHastaUtc.Should().BeNull();
        linea.EstaVigenteEn(DateTime.UtcNow).Should().BeTrue("el inicio nunca es futuro");
        linea.PagadorStripeCustomerId.Should().Be(LineaOutboundGestionPropiaDemoSeeder.PagadorDemo);
        linea.Tramo.Should().Be(TramoTrabajadoresActivos.De1A10);
        linea.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
    }

    [Fact]
    public async Task Ningun_otro_Tenant_de_la_base_tiene_la_linea_P()
    {
        var beta = (await IdsAsync(CoberturaPantallasSeeder.NombreTenantBeta)).Single();

        var conLineaP = new List<Guid>();
        foreach (var (id, _) in await TodosLosTenantsAsync())
            if ((await LineasDeAsync(id)).Any(p => p.Linea == LineaProductoContratado.OutboundGestionPropia))
                conLineaP.Add(id);

        conLineaP.Should().BeEquivalentTo(new[] { beta },
            "solo el Tenant de gestión propia la lleva: ni plataforma, ni Operador CAE externo, ni gestión externa, ni Inbound");
    }

    [Fact]
    public async Task Los_Tenants_excluidos_que_la_siembra_si_crea_no_la_tienen_y_el_TCE_conserva_solo_su_linea_Inbound()
    {
        // Control del instrumento: la prueba de arriba recorre todos los Tenants, pero esto demuestra que los excluidos
        // existen en esta base (un nombre que no existiera no probaría nada) y que se leen sus filas.
        foreach (var nombre in new[]
                 {
                     CoberturaPantallasSeeder.NombreTenantOperador, CoberturaPantallasSeeder.NombreTenantAlfa,
                     InboundDemoSeeder.NombreTenantMoncayo, InboundDemoSeeder.NombreTenantRibera, InboundDemoSeeder.NombreTenantSomontano
                 })
        {
            (await IdsAsync(nombre)).Should().ContainSingle($"{nombre} lo siembra esta base");
            (await LineasDeAsync(nombre)).Should().BeEmpty($"{nombre} no gestiona su CAE con su propio personal");
        }

        (await LineasDeAsync(InboundDemoSeeder.NombreTenantTce)).Should().ContainSingle()
            .Which.Linea.Should().Be(LineaProductoContratado.Inbound);
    }

    [Fact]
    public async Task Una_segunda_siembra_no_duplica_ninguna_linea()
    {
        await fixture.SembrarLineasAsync();
        await fixture.SembrarLineasAsync();

        (await LineasDeAsync(CoberturaPantallasSeeder.NombreTenantBeta)).Should().ContainSingle();
        (await LineasDeAsync(InboundDemoSeeder.NombreTenantTce)).Should().ContainSingle();
    }

    private async Task<List<ProductoContratado>> LineasDeAsync(string nombreTenant) =>
        await LineasDeAsync((await IdsAsync(nombreTenant)).Single());

    private async Task<List<ProductoContratado>> LineasDeAsync(Guid tenantId)
    {
        using var ambito = fixture.Arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await contexto.ProductosContratados.ToListAsync();
    }

    private async Task<List<Guid>> IdsAsync(string nombre)
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return await bootstrap.Tenants.Where(t => t.Nombre == nombre).Select(t => t.Id).ToListAsync();
    }

    private async Task<List<(Guid Id, string Nombre)>> TodosLosTenantsAsync()
    {
        await using var bootstrap = fixture.Arnes.Servicios.GetRequiredService<FabricaContextoDeBootstrap>().Crear();
        return (await bootstrap.Tenants.Select(t => new { t.Id, t.Nombre }).ToListAsync()).Select(t => (t.Id, t.Nombre)).ToList();
    }
}

/// <summary>
/// <b>Lo que acota la línea P cuando el Tenant no es suyo</b>, en una base aparte: un Tenant que se llama como el Tenant Beta
/// pero no lleva el marcador de datos de demo no recibe nada (podría ser real), el que sí lo lleva la recibe, y fuera de
/// Development no escribe aunque <c>DatosPrueba:Activo</c> esté activo.
/// </summary>
public sealed class LineaOutboundGestionPropiaDemoSeederAcotadaTests : IAsyncLifetime
{
    private ArnesDeArranqueRuntime _arnes = null!;
    private IConfiguration _configuracion = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        _configuracion = _arnes.Servicios.GetRequiredService<IConfiguration>();
    }

    public Task DisposeAsync() => _arnes.DisposeAsync().AsTask();

    private async Task<int> LineasPAsync(Guid tenantId)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(tenantId))
            return await contexto.ProductosContratados.CountAsync(p => p.Linea == LineaProductoContratado.OutboundGestionPropia);
    }

    [Fact]
    public async Task Un_Tenant_con_el_nombre_de_Beta_pero_sin_marcador_de_demo_no_recibe_la_linea_y_con_marcador_si()
    {
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var ajeno = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        using (AmbitoTenantExplicito.Establecer(ajeno.Id))
        {
            bootstrap.Tenants.Add(ajeno);
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        (await LineasPAsync(ajeno.Id)).Should().Be(0, "sin el marcador de datos de demo, el Tenant podría ser real");

        // El mismo Tenant, ya marcado: la recibe, una sola vez.
        using (AmbitoTenantExplicito.Establecer(ajeno.Id))
        {
            ajeno.MarcarDatosDemoCompletados();
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, EntornoDePrueba.Desarrollo, NullLogger.Instance);
        (await LineasPAsync(ajeno.Id)).Should().Be(1);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task Fuera_de_Development_no_escribe_aunque_el_Tenant_este_marcado(string entorno)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        await using var bootstrap = ambito.ServiceProvider.GetRequiredService<FabricaContextoDeBootstrap>().Crear();

        var marcado = new Tenant(CoberturaPantallasSeeder.NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto);
        marcado.MarcarDatosDemoCompletados();
        using (AmbitoTenantExplicito.Establecer(marcado.Id))
        {
            bootstrap.Tenants.Add(marcado);
            await bootstrap.SaveChangesAsync();
        }

        await LineaOutboundGestionPropiaDemoSeeder.SeedAsync(bootstrap, _configuracion, new EntornoDePrueba(entorno), NullLogger.Instance);

        (await LineasPAsync(marcado.Id)).Should().Be(0);
    }
}
