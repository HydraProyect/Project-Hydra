using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using CaeManager.Infrastructure.Dashboard;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Dashboard;

public class CierresMensualesAislamientoTests
{
    [Fact]
    public async Task Rls_solo_permite_leer_y_reabrir_el_cierre_del_Tenant_propietario()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var tenantA = new Tenant("Operación A");
        var tenantB = new Tenant("Operación B");
        var tenantActual = new TenantPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(arnes.CadenaPropietario, p => p.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual)).Options;
        await using (var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual))
        {
            propietario.Tenants.AddRange(tenantA, tenantB);
            await propietario.SaveChangesAsync();
            foreach (var tenant in new[] { tenantA, tenantB })
            {
                using var ambito = AmbitoTenantExplicito.Establecer(tenant.Id);
                propietario.CierresMensualesTenantBeneficiario.Add(CierreMensualTenantBeneficiario.Crear(2026, 9, "{}", false, DateTime.UtcNow));
                await propietario.SaveChangesAsync();
            }
        }
        using (AmbitoTenantExplicito.Establecer(tenantA.Id))
        using (var scope = arnes.Servicios.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var visibles = await runtime.CierresMensualesTenantBeneficiario.IgnoreQueryFilters().ToListAsync();
            visibles.Should().ContainSingle().Which.TenantId.Should().Be(tenantA.Id);
            (await runtime.CierresMensualesTenantBeneficiario.AnyAsync(c => c.TenantId == tenantB.Id)).Should().BeFalse();
            visibles[0].Reabrir("Corrección del mes", DateTime.UtcNow);
            await runtime.SaveChangesAsync();
        }
        using (AmbitoTenantExplicito.Establecer(tenantB.Id))
        using (var scope = arnes.Servicios.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var cierre = await runtime.CierresMensualesTenantBeneficiario.SingleAsync();
            cierre.TenantId.Should().Be(tenantB.Id);
            cierre.ReabiertoEnUtc.Should().BeNull();
        }
    }

    [Fact]
    public async Task El_servicio_cierra_el_mes_anterior_una_vez_y_vuelve_a_cerrar_el_mes_reabierto()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var tenant = new Tenant("Operación cierre");
        var tenantActual = new TenantPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(arnes.CadenaPropietario, p => p.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual)).Options;
        await using (var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual))
        {
            propietario.Tenants.Add(tenant);
            await propietario.SaveChangesAsync();
        }

        var servicio = ActivatorUtilities.CreateInstance<CierreMensualCuadroDireccionHostedService>(arnes.Servicios);
        await servicio.ProcesarAsync(CancellationToken.None);
        await servicio.ProcesarAsync(CancellationToken.None);

        var ahora = DiaDeNegocio.De(DateTime.UtcNow);
        var mesAnterior = new DateOnly(ahora.Year, ahora.Month, 1).AddMonths(-1);
        DateTime cerradoPrimero;
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        using (var scope = arnes.Servicios.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var cierre = await runtime.CierresMensualesTenantBeneficiario.SingleAsync();
            cierre.TenantId.Should().Be(tenant.Id);
            (cierre.Anyo, cierre.Mes).Should().Be((mesAnterior.Year, mesAnterior.Month));
            cierre.Estimado.Should().Be(new MesCuadroDireccion(mesAnterior.Year, mesAnterior.Month).CierreEsEstimado);
            cerradoPrimero = cierre.CerradoEnUtc;
            cierre.Reabrir("Corrección del mes", DateTime.UtcNow);
            await runtime.SaveChangesAsync();
        }

        await servicio.ProcesarAsync(CancellationToken.None);

        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        using (var scope = arnes.Servicios.CreateScope())
        {
            var runtime = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var cierre = await runtime.CierresMensualesTenantBeneficiario.SingleAsync();
            cierre.ReabiertoEnUtc.Should().BeNull("el cierre siguiente vuelve a congelar el mes reabierto");
            cierre.CerradoEnUtc.Should().BeAfter(cerradoPrimero);
        }
    }

    private sealed class TenantPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
