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
    public async Task El_servicio_cierra_el_mes_anterior_una_vez()
    {
        // Composición real (AddApplication + AddInfrastructure): el cierre resuelve la medición de Application.
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var tenant = await PrimerTenantAsync(arnes);
        var servicio = CrearServicio(arnes, TimeProvider.System);
        await servicio.ProcesarAsync(CancellationToken.None);
        await servicio.ProcesarAsync(CancellationToken.None);

        var ahora = DiaDeNegocio.De(DateTime.UtcNow);
        var mesAnterior = new DateOnly(ahora.Year, ahora.Month, 1).AddMonths(-1);
        var cierre = await LeerCierreAsync(arnes, tenant, mesAnterior);
        cierre.TenantId.Should().Be(tenant.Id);
        cierre.Estimado.Should().Be(new MesCuadroDireccion(mesAnterior.Year, mesAnterior.Month).CierreEsEstimado);
    }

    [Fact]
    public async Task Un_mes_reabierto_este_mes_sigue_abierto_tras_otra_vuelta_aunque_en_UTC_caiga_en_el_mes_anterior()
    {
        // El mes M se cierra en la primera pasada con el reloj dentro del mes M+1; la reapertura ocurre 10 minutos
        // después de las 00:00 de Madrid del día 1 de M+1 (en UTC puede ser aún el último día de M).
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var tenant = await PrimerTenantAsync(arnes);
        var mes = MesDelTenant();
        var inicioSiguiente = DiaDeNegocio.InicioEnUtc(mes.AddMonths(1));
        var reabierto = await CerrarYReabrirAsync(arnes, tenant, mes, inicioSiguiente.AddMinutes(5), inicioSiguiente.AddMinutes(10));

        await CrearServicio(arnes, new RelojFijo(inicioSiguiente.AddHours(1))).ProcesarAsync(CancellationToken.None);

        var cierre = await LeerCierreAsync(arnes, tenant, mes);
        cierre.ReabiertoEnUtc.Should().Be(reabierto, "la reapertura sigue vigente hasta el próximo día 1 de Madrid");
    }

    [Fact]
    public async Task Un_mes_reabierto_el_mes_pasado_se_recierra_aunque_la_reapertura_fuera_a_las_23_30_del_ultimo_dia_de_Madrid()
    {
        await using var arnes = await ArnesPilotoOutbound.CrearAsync();
        var tenant = await PrimerTenantAsync(arnes);
        var mes = MesDelTenant();
        var inicioSiguiente = DiaDeNegocio.InicioEnUtc(mes.AddMonths(1));
        var inicioSubsiguiente = DiaDeNegocio.InicioEnUtc(mes.AddMonths(2));
        await CerrarYReabrirAsync(arnes, tenant, mes, inicioSiguiente.AddMinutes(5), inicioSubsiguiente.AddMinutes(-30));

        await CrearServicio(arnes, new RelojFijo(inicioSubsiguiente.AddHours(1))).ProcesarAsync(CancellationToken.None);

        var cierre = await LeerCierreAsync(arnes, tenant, mes);
        cierre.ReabiertoEnUtc.Should().BeNull("la reapertura es de un día de negocio anterior al día 1 del mes actual");
    }

    // El Tenant del arnés nace ahora: el mes de su alta es el primero que la recuperación mira.
    private static DateOnly MesDelTenant()
    {
        var hoy = DiaDeNegocio.De(DateTime.UtcNow);
        return new DateOnly(hoy.Year, hoy.Month, 1);
    }

    private static CierreMensualCuadroDireccionHostedService CrearServicio(ArnesPilotoOutbound arnes, TimeProvider reloj) =>
        new(arnes.FabricaDeAmbitos, new LiderSiempre(), reloj,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CierreMensualCuadroDireccionHostedService>.Instance);

    private static async Task<Tenant> PrimerTenantAsync(ArnesPilotoOutbound arnes)
    {
        using var scope = arnes.Servicios.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CaeManager.Application.Tenants.ITenantsQueryContext>()
            .Tenants.OrderBy(t => t.Id).FirstAsync();
    }

    private static async Task<CierreMensualTenantBeneficiario> LeerCierreAsync(ArnesPilotoOutbound arnes, Tenant tenant, DateOnly mes)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenant.Id);
        using var scope = arnes.Servicios.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        return await runtime.CierresMensualesTenantBeneficiario.AsNoTracking().SingleAsync(c => c.Anyo == mes.Year && c.Mes == mes.Month);
    }

    private static async Task<DateTime> CerrarYReabrirAsync(ArnesPilotoOutbound arnes, Tenant tenant, DateOnly mes, DateTime cierraEn, DateTime reabreEn)
    {
        await CrearServicio(arnes, new RelojFijo(cierraEn)).ProcesarAsync(CancellationToken.None);
        reabreEn = DateTime.SpecifyKind(reabreEn, DateTimeKind.Utc);
        using var ambito = AmbitoTenantExplicito.Establecer(tenant.Id);
        using var scope = arnes.Servicios.CreateScope();
        var runtime = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var cierre = await runtime.CierresMensualesTenantBeneficiario.SingleAsync(c => c.Anyo == mes.Year && c.Mes == mes.Month);
        cierre.Reabrir("Corrección del mes", reabreEn);
        await runtime.SaveChangesAsync();
        return reabreEn;
    }

    private sealed class RelojFijo(DateTime instanteUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(instanteUtc, DateTimeKind.Utc));
    }

    private sealed class LiderSiempre : CaeManager.Infrastructure.Coordinacion.IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }

    private sealed class TenantPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
