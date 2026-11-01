using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// La seguridad de acceso del Tenant bajo el rol runtime, con los interceptores y la RLS de
/// producción. Sin fila visible la fábrica cae en los valores por defecto sin avisar, así que
/// una política que ocultara la fila apagaría la exigencia en silencio: solo este arnés lo ve.
/// </summary>
public class SeguridadAccesoBajoRolRuntimeTests
{
    [Fact]
    public async Task La_fabrica_lee_el_ajuste_del_Tenant_propietario_bajo_RLS()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            serviciosAdicionales: servicios => servicios
                .AddIdentityCore<ApplicationUser>()
                .AddRoles<IdentityRole<Guid>>()
                .AddEntityFrameworkStores<CaeManagerDbContext>()
                .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>());

        var cuenta = await SembrarAsync(arnes.CadenaPropietario);

        using var ambito = arnes.Servicios.CreateScope();
        var principal = await ambito.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>()
            .CreateAsync(cuenta);

        principal.FindFirst(TenantClaimsPrincipalFactory.TipoClaimMinutosInactividad)?.Value.Should().Be("45");
        principal.FindFirst(TenantClaimsPrincipalFactory.TipoClaimActivacionPendiente)?.Value
            .Should().Be(TenantClaimsPrincipalFactory.ActivacionPendienteDosFactores);
    }

    private static async Task<ApplicationUser> SembrarAsync(string cadenaPropietario)
    {
        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        await using var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);

        var tenant = new Tenant("Tenant que exige segundo factor");
        propietario.Tenants.Add(tenant);
        await propietario.SaveChangesAsync();

        using var _ = AmbitoTenantExplicito.Establecer(tenant.Id);
        var parametros = new ParametroSistema(30, 15);
        parametros.ActualizarSeguridadAcceso(45, exigeSegundoFactorTodosLosPerfiles: true);
        propietario.ParametrosSistema.Add(parametros);

        var correo = $"{Guid.NewGuid():N}@seguridad.local";
        var cuenta = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            UserName = correo,
            NormalizedUserName = correo.ToUpperInvariant(),
            Email = correo,
            NormalizedEmail = correo.ToUpperInvariant(),
            NombreCompleto = "Usuario",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
        };
        propietario.Users.Add(cuenta);
        await propietario.SaveChangesAsync();
        return cuenta;
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
