using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// El claim <c>requiere_activacion</c> se sella de verdad, contra un
/// <c>UserManager</c> real.
///
/// <para>
/// <b>Por qué no basta con los tests del middleware.</b>
/// <c>CuentaAMedioActivarSinAccesoMiddleware</c> se prueba en aislamiento
/// inyectando el claim a mano. Si <see cref="TenantClaimsPrincipalFactory"/>
/// dejara de ponerlo —o lo pusiera con otro valor, u <c>IsInRole</c> dejara de
/// reconocer el rol porque cambió el <c>RoleClaimType</c>—, el middleware no
/// bloquearía nunca, la contraseña temporal volvería a alcanzar la descarga de
/// PDFs, y los doce tests de aquel seguirían en verde. Los dos extremos del
/// contrato tienen que observarse, no solo el que es cómodo de montar.
/// </para>
/// </summary>
public class ClaimDeActivacionDeCuentaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private ServiceProvider _servicios = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(new TenantActualFijo());
        servicios.AddScoped<PuertaAccesoDatos>();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        // La factory REAL, la misma que registra Program.cs. Sustituirla por
        // una copia del cálculo sería probar el test, no el producto.
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var tenant = new Tenant("Tenant de prueba");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenant = tenant.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_contrasena_temporal_sin_cambiar_marca_la_cuenta()
    {
        var principal = await PrincipalDeAsync(
            "temporal@x.test", Roles.GestorCae, debeCambiarContrasena: true, dosFactores: false);

        RequiereActivacion(principal).Should().BeTrue();
    }

    [Fact]
    public async Task Un_administrador_sin_dos_factores_marca_la_cuenta()
    {
        var principal = await PrincipalDeAsync(
            "admin-sin-2fa@x.test", Roles.Administrador, debeCambiarContrasena: false, dosFactores: false);

        RequiereActivacion(principal).Should().BeTrue(
            "la 2FA es obligatoria para el rol con más alcance del sistema (P1-13)");
    }

    [Fact]
    public async Task Un_administrador_con_dos_factores_no_marca_la_cuenta()
    {
        var principal = await PrincipalDeAsync(
            "admin-con-2fa@x.test", Roles.Administrador, debeCambiarContrasena: false, dosFactores: true);

        RequiereActivacion(principal).Should().BeFalse();
    }

    [Fact]
    public async Task Un_rol_que_no_es_administrador_no_necesita_dos_factores()
    {
        // Discrimina de verdad: si la condición fuera "cualquiera sin 2FA",
        // este daría true y toda la plantilla quedaría bloqueada.
        var principal = await PrincipalDeAsync(
            "gestor@x.test", Roles.GestorCae, debeCambiarContrasena: false, dosFactores: false);

        RequiereActivacion(principal).Should().BeFalse();
    }

    [Fact]
    public async Task El_claim_de_tenant_sigue_sellandose()
    {
        // La factory tenía un solo cometido antes de esto; que el nuevo no se
        // haya llevado por delante al viejo no se da por hecho.
        var principal = await PrincipalDeAsync(
            "con-tenant@x.test", Roles.Consulta, debeCambiarContrasena: false, dosFactores: false);

        principal.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)!.Value
            .Should().Be(_tenant.ToString());
    }

    private static bool RequiereActivacion(System.Security.Claims.ClaimsPrincipal principal) =>
        principal.HasClaim(TenantClaimsPrincipalFactory.TipoClaimRequiereActivacion, "true");

    private async Task<System.Security.Claims.ClaimsPrincipal> PrincipalDeAsync(
        string email, string rol, bool debeCambiarContrasena, bool dosFactores)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = _tenant,
            DebeCambiarContrasena = debeCambiarContrasena,
            TwoFactorEnabled = dosFactores,
        };

        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();

        var factory = ambito.ServiceProvider
            .GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();

        factory.Should().BeOfType<TenantClaimsPrincipalFactory>(
            "si el contenedor resolviera otra factory, este test estaría midiendo la de Identity");

        return await factory.CreateAsync(usuario);
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
