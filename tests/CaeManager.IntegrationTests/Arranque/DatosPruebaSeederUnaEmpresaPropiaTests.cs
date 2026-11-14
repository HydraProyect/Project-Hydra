using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
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
/// Decisión D-P11 (2026-11-14, opción B): un Tenant propietario solo tiene una Empresa propia, también el sembrado por
/// <see cref="DatosPruebaSeeder"/> y por <see cref="DelegacionDemoSeeder"/> (que reutiliza su siembra en varios Tenants).
/// Antes la siembra creaba 24 (o 9) Empresas propias por Tenant, cada una con sus Centros y Trabajadores. Estos tests miden
/// el resultado de la siembra, no el código: la prueba de arquitectura
/// <c>AltaDeEmpresaPropiaSoloTrasElLimiteTests</c> solo ve qué ficheros construyen una Empresa, no cuántas.
/// </summary>
public class DatosPruebaSeederUnaEmpresaPropiaTests
{
    [Fact]
    public async Task La_siembra_completa_deja_una_sola_Empresa_propia_con_todos_los_Centros_y_Trabajadores_colgados_de_ella()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var tenantId = await CrearTenantConCatalogoAsync(arnes, "Tenant Una Propia A");

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var resumen = await DatosPruebaSeeder.SembrarSoloDatosCompletosAsync(contexto, NullLogger.Instance);
                resumen.Should().NotBeNull("el Tenant es nuevo, con catálogo y sin Clientes");
            }
        }

        await ComprobarUnaSolaPropiaConTodoColgadoDeEllaAsync(arnes, tenantId, clientesEsperados: 9, subcontratasEsperadas: 8);
    }

    [Fact]
    public async Task La_siembra_de_datos_del_Tenant_adicional_deja_una_sola_Empresa_propia_con_todos_los_Centros_y_Trabajadores_colgados_de_ella()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);
        var tenantId = await CrearTenantConCatalogoAsync(arnes, "Tenant Una Propia B");

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var resumen = await DatosPruebaSeeder.SembrarSoloDatosAsync(contexto, NullLogger.Instance);
                resumen.Should().NotBeNull("el Tenant es nuevo, con catálogo y sin Clientes");
            }
        }

        await ComprobarUnaSolaPropiaConTodoColgadoDeEllaAsync(arnes, tenantId, clientesEsperados: 3, subcontratasEsperadas: 3);
    }

    /// <summary>
    /// La siembra de la demo de delegación: cada Tenant que siembra (los Clientes delegantes y el propio Tenant de
    /// demostración) termina con una Empresa propia como máximo, y al menos uno con exactamente una (control positivo:
    /// un recuento de ceros en todos los Tenants no probaría nada).
    /// </summary>
    [Fact]
    public async Task La_siembra_de_la_demo_de_delegacion_no_deja_ningun_Tenant_con_mas_de_una_Empresa_propia()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: true);

        using (var ambito = arnes.Servicios.CreateScope())
        {
            var sp = ambito.ServiceProvider;
            await DelegacionDemoSeeder.SeedAsync(
                sp.GetRequiredService<CaeManagerDbContext>(),
                sp.GetRequiredService<UserManager<ApplicationUser>>(),
                sp.GetRequiredService<IUserStore<ApplicationUser>>(),
                sp.GetRequiredService<IConfiguration>(),
                EntornoDePrueba.Desarrollo,
                NullLogger.Instance);
        }

        using var lectura = arnes.Servicios.CreateScope();
        var lector = lectura.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var tenants = await lector.Tenants.AsNoTracking().Select(t => new { t.Id, t.Nombre }).ToListAsync();

        var propiasPorTenant = new List<(string Nombre, int Propias)>();
        foreach (var tenant in tenants)
        {
            using (AmbitoTenantExplicito.Establecer(tenant.Id))
            {
                var propias = await lector.Empresas.IgnoreQueryFilters()
                    .CountAsync(e => e.TenantId == tenant.Id && e.EsPropia && !e.EstaEliminado);
                propiasPorTenant.Add((tenant.Nombre, propias));
            }
        }

        propiasPorTenant.Where(t => t.Propias > 1).Should().BeEmpty(
            "D-P11: ningún Tenant sembrado puede tener más de una Empresa propia activa");
        propiasPorTenant.Count(t => t.Propias == 1).Should().BeGreaterThan(0,
            "control positivo: la demo de delegación siembra al menos un Tenant con su Empresa propia");
    }

    private static async Task ComprobarUnaSolaPropiaConTodoColgadoDeEllaAsync(
        ArnesDeArranqueRuntime arnes, Guid tenantId, int clientesEsperados, int subcontratasEsperadas)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            var propias = await contexto.Empresas.AsNoTracking().Where(e => e.EsPropia).ToListAsync();
            propias.Should().ContainSingle("D-P11: la siembra deja una sola Empresa propia en el Tenant");
            var propiaId = propias.Single().Id;

            var centros = await contexto.Centros.AsNoTracking().Select(c => c.EmpresaId).ToListAsync();
            centros.Should().NotBeEmpty("la siembra tiene Centros");
            centros.Should().OnlyContain(id => id == propiaId, "todos los Centros cuelgan de la única Empresa propia");

            var trabajadores = await contexto.Trabajadores.AsNoTracking()
                .Select(t => new { t.EmpresaId, t.SubcontrataId }).ToListAsync();
            trabajadores.Count(t => t.EmpresaId != null).Should().BeGreaterThan(0, "la Empresa propia tiene plantilla");
            trabajadores.Where(t => t.EmpresaId != null).Should().OnlyContain(
                t => t.EmpresaId == propiaId, "todo Trabajador de una Empresa cuelga de la única Empresa propia");

            var vehiculos = await contexto.Vehiculos.AsNoTracking().Select(v => v.EmpresaId).ToListAsync();
            vehiculos.Should().OnlyContain(id => id == propiaId, "los Vehículos de la siembra son de la Empresa propia");

            var clientes = await contexto.Empresas.AsNoTracking().Where(e => e.EsCritico != null).Select(e => e.Id).ToListAsync();
            clientes.Should().HaveCount(clientesEsperados);
            var subcontratas = await contexto.Empresas.AsNoTracking().Where(e => e.NivelServicio != null).Select(e => e.Id).ToListAsync();
            subcontratas.Should().HaveCount(subcontratasEsperadas);

            var relaciones = await contexto.RelacionesEmpresariales.AsNoTracking()
                .Select(r => new { r.ProveedoraId, r.ClienteId }).ToListAsync();
            clientes.Should().OnlyContain(
                clienteId => relaciones.Any(r => r.ProveedoraId == propiaId && r.ClienteId == clienteId),
                "la Empresa propia presta servicio a cada Cliente empresarial de la siembra");
            subcontratas.Should().OnlyContain(
                subcontrataId => relaciones.Any(r => r.ProveedoraId == subcontrataId && r.ClienteId == propiaId),
                "cada Subcontrata cuelga de la única Empresa propia");
        }
    }

    private static async Task<Guid> CrearTenantConCatalogoAsync(ArnesDeArranqueRuntime arnes, string nombreTenant)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var tenant = new Tenant(nombreTenant);
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            contexto.Tenants.Add(tenant);
            contexto.ParametrosSistema.Add(new ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));
            contexto.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());
            await contexto.SaveChangesAsync();
        }

        return tenant.Id;
    }
}
