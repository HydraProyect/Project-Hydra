using CaeManager.Application.Common;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// P1-11 de docs/business/MATURITY_REVIEW.md: <c>PuertaAccesoDatos</c> pasó
/// de serializar con un <c>SemaphoreSlim(1,1)</c> compartido a dar a cada
/// operación su propio <c>CaeManagerDbContext</c> vía
/// <c>IDbContextFactory{CaeManagerDbContext}</c>. Esta suite prueba contra
/// PostgreSQL real exactamente lo que un test con mocks no puede: que la
/// fábrica, registrada <c>Scoped</c>, resuelve sus interceptores (y con
/// ellos <c>ITenantActual</c>) contra el scope correcto — el riesgo real de
/// este cambio era una fuga de tenant si la fábrica hubiera quedado
/// Singleton (ver el comentario de <c>PuertaAccesoDatos</c>).
/// </summary>
public class PuertaAccesoDatosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>()
                .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
                .Options,
            new EphemeralDataProtectionProvider(),
            new TenantActualAmbiental());

        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Construye un contenedor con exactamente el mismo registro que
    /// <c>InfrastructureServiceCollectionExtensions</c>: fábrica Scoped +
    /// interceptores + <c>PuertaAccesoDatos</c>. Cada llamada simula un
    /// circuito de Blazor distinto — un <see cref="IServiceScope"/> propio,
    /// con su propio <see cref="TenantActualAmbiental"/> mutable.
    /// </summary>
    private ServiceProvider ConstruirContenedor()
    {
        var servicios = new ServiceCollection();

        servicios.AddScoped<ITenantActual, TenantActualAmbiental>();
        servicios.AddScoped<ICurrentUserService>(_ => new CurrentUserServiceFalso());
        servicios.AddScoped<AuditoriaInterceptor>();
        servicios.AddScoped<TenantSelladoInterceptor>();
        servicios.AddSingleton<ConcurrenciaOptimistaInterceptor>();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());

        servicios.AddDbContextFactory<CaeManagerDbContext>((sp, opciones) =>
        {
            opciones.UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
            opciones.AddInterceptors(
                sp.GetRequiredService<AuditoriaInterceptor>(),
                sp.GetRequiredService<TenantSelladoInterceptor>(),
                sp.GetRequiredService<ConcurrenciaOptimistaInterceptor>());
        }, lifetime: ServiceLifetime.Scoped);

        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddScoped<IPuertaAccesoDatos>(sp => sp.GetRequiredService<PuertaAccesoDatos>());

        // Mismo registro que InfrastructureServiceCollectionExtensions: fuera
        // de un EjecutarAsync, memoiza uno por scope (no lo necesita ningún
        // test de aquí, pero mantiene el contenedor fiel al real).
        servicios.AddScoped<CaeManagerDbContext>(sp =>
            sp.GetRequiredService<PuertaAccesoDatos>().ContextoActual
            ?? sp.GetRequiredService<IDbContextFactory<CaeManagerDbContext>>().CreateDbContext());

        return servicios.BuildServiceProvider();
    }

    [Fact]
    public async Task Dos_operaciones_no_anidadas_reciben_contextos_distintos()
    {
        // Es la prueba directa de que ya no hay un único DbContext compartido
        // para todo el circuito: dos Commands/Queries que antes competían por
        // el mismo objeto ahora ni se conocen.
        await using var contenedor = ConstruirContenedor();
        using var scope = contenedor.CreateScope();
        var puerta = scope.ServiceProvider.GetRequiredService<IPuertaAccesoDatos>();

        var primero = await puerta.EjecutarAsync(() => Task.FromResult(ObtenerContextoActual(scope)));
        var segundo = await puerta.EjecutarAsync(() => Task.FromResult(ObtenerContextoActual(scope)));

        primero.Should().NotBeSameAs(segundo);
    }

    [Fact]
    public async Task Una_llamada_anidada_reutiliza_el_contexto_de_la_exterior()
    {
        // Reentrancia necesaria para ObtenerKpisGlobalesQueryHandler (un
        // handler que despacha otro Send dentro de sí) — sin esto, la query
        // interna vería datos aún no guardados por la externa, o peor, dos
        // DbContext abriendo transacciones simultáneas sobre la misma unidad
        // de trabajo lógica.
        await using var contenedor = ConstruirContenedor();
        using var scope = contenedor.CreateScope();
        var puerta = scope.ServiceProvider.GetRequiredService<IPuertaAccesoDatos>();

        CaeManagerDbContext? interno = null;
        var externo = await puerta.EjecutarAsync(async () =>
        {
            var contextoExterno = ObtenerContextoActual(scope);
            interno = await puerta.EjecutarAsync(() => Task.FromResult(ObtenerContextoActual(scope)));
            return contextoExterno;
        });

        interno.Should().BeSameAs(externo);
    }

    [Fact]
    public async Task El_contexto_se_desecha_al_terminar_la_operacion()
    {
        await using var contenedor = ConstruirContenedor();
        using var scope = contenedor.CreateScope();
        var puerta = scope.ServiceProvider.GetRequiredService<IPuertaAccesoDatos>();

        var contexto = await puerta.EjecutarAsync(() => Task.FromResult(ObtenerContextoActual(scope)));

        var acto = () => contexto.Clientes.CountAsync();
        await acto.Should().ThrowAsync<ObjectDisposedException>(
            "un contexto fuera de su EjecutarAsync no debe seguir siendo usable — evita que alguien "
            + "se quede una referencia y la reutilice después de que la operación terminó");
    }

    [Fact]
    public async Task La_fabrica_scoped_no_mezcla_el_tenant_de_dos_circuitos_distintos()
    {
        // El riesgo real de este cambio: si IDbContextFactory hubiera quedado
        // Singleton (el valor por defecto), TenantSelladoInterceptor habría
        // resuelto ITenantActual una sola vez, contra el primer scope que
        // creara un contexto, y lo habría reutilizado para todos los
        // circuitos posteriores — fuga de tenant en la frontera de
        // aislamiento más sensible del sistema (docs/MULTITENANCY.md).
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var contenedorA = ConstruirContenedor();
        using var scopeA = contenedorA.CreateScope();
        ((TenantActualAmbiental)scopeA.ServiceProvider.GetRequiredService<ITenantActual>()).TenantId = tenantA;
        var puertaA = scopeA.ServiceProvider.GetRequiredService<IPuertaAccesoDatos>();

        var idClienteA = await puertaA.EjecutarAsync(async () =>
        {
            var contexto = ObtenerContextoActual(scopeA);
            var cliente = new Cliente("Cliente del tenant A", "B00000001", esCritico: false);
            contexto.Clientes.Add(cliente);
            await contexto.SaveChangesAsync();
            return cliente.Id;
        });

        await using var contenedorB = ConstruirContenedor();
        using var scopeB = contenedorB.CreateScope();
        ((TenantActualAmbiental)scopeB.ServiceProvider.GetRequiredService<ITenantActual>()).TenantId = tenantB;
        var puertaB = scopeB.ServiceProvider.GetRequiredService<IPuertaAccesoDatos>();

        var visibleDesdeB = await puertaB.EjecutarAsync(async () =>
        {
            var contexto = ObtenerContextoActual(scopeB);
            return await contexto.Clientes.AnyAsync(c => c.Id == idClienteA);
        });

        visibleDesdeB.Should().BeFalse("el filtro global de tenant tiene que aislar lo creado en el circuito A");

        var selladoConA = await puertaA.EjecutarAsync(async () =>
        {
            var contexto = ObtenerContextoActual(scopeA);
            var cliente = await contexto.Clientes.FindAsync(idClienteA);
            return cliente!.TenantId;
        });

        selladoConA.Should().Be(tenantA);
    }

    private static CaeManagerDbContext ObtenerContextoActual(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
}
