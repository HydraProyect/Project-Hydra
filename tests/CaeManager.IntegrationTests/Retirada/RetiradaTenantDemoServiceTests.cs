using CaeManager.Application.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Retirada;

/// <summary>
/// La única garantía que <see cref="RetiradaTenantDemoService"/> existe para
/// dar: no puede alcanzar un tenant que no sea de demo. Los dos primeros
/// tests son los que se falsaron por mutación a mano (invertir el
/// <c>Contains</c>/<c>EsPlataforma</c> del servicio) para confirmar que
/// fallan por el motivo correcto antes de confiar en ellos — ver el informe
/// de la sesión para el registro de esa mutación.
/// </summary>
public class RetiradaTenantDemoServiceTests
{
    [Fact]
    public async Task La_retirada_rechaza_el_tenant_de_plataforma()
    {
        // El tenant #1 llega por HasData de migración (ver
        // TenantConfiguration) — no hace falta sembrar nada más para que
        // exista con EsPlataforma=true.
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var intentar = async () => await RetiradaTenantDemoService.RetirarAsync(
            contexto, TenantSeedData.IdPorDefecto, NullLogger.Instance);

        await intentar.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*plataforma*");

        (await contexto.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == TenantSeedData.IdPorDefecto))
            .Should().BeTrue("MEDIDO: el rechazo pasa ANTES de borrar nada — el tenant de plataforma sigue existiendo");
    }

    [Fact]
    public async Task La_retirada_rechaza_un_tenant_que_no_esta_en_la_allowlist_de_demo()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        Guid tenantRealId;
        using (var ambitoSiembra = arnes.Servicios.CreateScope())
        {
            var contexto = ambitoSiembra.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            // Nombre deliberadamente parecido a uno de demo ("... demo ..." no
            // aparece, pero comparte formato "S.L. (...)") — la allowlist
            // exige coincidencia EXACTA, no un parecido razonable.
            var tenantReal = new Tenant("Cliente Real Contratado S.L.");
            tenantRealId = tenantReal.Id;

            using (AmbitoTenantExplicito.Establecer(tenantReal.Id))
            {
                contexto.Tenants.Add(tenantReal);
                await contexto.SaveChangesAsync();
            }
        }

        using var ambito = arnes.Servicios.CreateScope();
        var contextoLectura = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var intentar = async () => await RetiradaTenantDemoService.RetirarAsync(
            contextoLectura, tenantRealId, NullLogger.Instance);

        await intentar.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*no está en la lista de tenants de demo conocidos*");

        (await contextoLectura.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantRealId))
            .Should().BeTrue("MEDIDO: un tenant fuera de la allowlist se queda intacto, aunque su nombre se parezca al de un demo");
    }

    /// <summary>
    /// La prueba positiva completa: siembra los tenants de demo reales (mismo
    /// camino que producción), retira SOLO Laboratorios Dexter, y comprueba
    /// las dos mitades de la garantía a la vez — borra todo lo suyo (tenant,
    /// usuarios, cartera) y no toca ni una fila de Refrielectric, el tenant
    /// hermano sembrado en la misma pasada.
    /// </summary>
    [Fact]
    public async Task La_retirada_borra_por_completo_un_tenant_de_demo_sin_tocar_a_su_hermano()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        using (var ambitoSiembra = arnes.Servicios.CreateScope())
        {
            var sp = ambitoSiembra.ServiceProvider;
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                sp.GetRequiredService<IConfiguration>(),
                EntornoDePrueba.Desarrollo,
                NullLogger.Instance);
        }

        Guid tenantDexterId;
        Guid tenantRefrielectricId;
        int empresasRefrielectricAntes;
        int usuariosDexterAntes;

        using (var ambitoLectura = arnes.Servicios.CreateScope())
        {
            var contexto = ambitoLectura.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            tenantDexterId = (await contexto.Tenants.SingleAsync(t => t.Nombre == DelegacionDemoSeeder.NombreTenantClienteDemo)).Id;
            tenantRefrielectricId = (await contexto.Tenants.SingleAsync(t => t.Nombre == DelegacionDemoSeeder.NombreTenantRefrielectric)).Id;

            usuariosDexterAntes = await contexto.Users.CountAsync(u => u.TenantId == tenantDexterId);
            usuariosDexterAntes.Should().BeGreaterThan(0, "el escenario solo es interesante si Dexter tenía usuarios antes de retirarlo");

            using (AmbitoTenantExplicito.Establecer(tenantRefrielectricId))
                empresasRefrielectricAntes = await contexto.Empresas.CountAsync();
            empresasRefrielectricAntes.Should().BeGreaterThan(0);
        }

        RetiradaTenantDemoService.ResultadoRetirada resultado;
        using (var ambitoRetirada = arnes.Servicios.CreateScope())
        {
            var contexto = ambitoRetirada.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            resultado = await RetiradaTenantDemoService.RetirarAsync(contexto, tenantDexterId, NullLogger.Instance);
        }

        resultado.FilasBorradas.Should().BeGreaterThan(0);
        resultado.UsuariosBorrados.Should().Be(usuariosDexterAntes);

        using var ambitoFinal = arnes.Servicios.CreateScope();
        var contextoFinal = ambitoFinal.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        // ── Mitad 1: Dexter no deja NADA ─────────────────────────────────
        (await contextoFinal.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantDexterId))
            .Should().BeFalse("MEDIDO: la fila del propio Tenant desaparece");

        (await contextoFinal.Users.CountAsync(u => u.TenantId == tenantDexterId)).Should().Be(0);

        using (AmbitoTenantExplicito.Establecer(tenantDexterId))
        {
            (await contextoFinal.Empresas.IgnoreQueryFilters().CountAsync(e => e.TenantId == tenantDexterId)).Should().Be(0);
            (await contextoFinal.Trabajadores.IgnoreQueryFilters().CountAsync(t => t.TenantId == tenantDexterId)).Should().Be(0);
            (await contextoFinal.Documentos.IgnoreQueryFilters().CountAsync(d => d.TenantId == tenantDexterId)).Should().Be(0);
            (await contextoFinal.TiposDocumento.IgnoreQueryFilters().CountAsync(t => t.TenantId == tenantDexterId)).Should().Be(0);
        }

        (await contextoFinal.DelegacionesTenant.AnyAsync(d => d.TenantClienteId == tenantDexterId || d.TenantConsultoraId == tenantDexterId))
            .Should().BeFalse("las delegaciones comerciales/de soporte que nombran a Dexter tampoco quedan huérfanas");

        // ── Mitad 2: Refrielectric, sembrado en la misma pasada, queda intacto ──
        (await contextoFinal.Tenants.IgnoreQueryFilters().AnyAsync(t => t.Id == tenantRefrielectricId))
            .Should().BeTrue("MEDIDO: retirar Dexter no toca al tenant hermano");

        using (AmbitoTenantExplicito.Establecer(tenantRefrielectricId))
        {
            (await contextoFinal.Empresas.CountAsync()).Should().Be(empresasRefrielectricAntes,
                "MEDIDO: ni una fila de Refrielectric se pierde al retirar Dexter");
        }
    }
}
