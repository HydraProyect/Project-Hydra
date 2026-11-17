using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace CaeManager.Web.Tests;

/// <summary>
/// Guarda de la Sesión Privilegiada en el interceptor de RLS (D-6 b, R-0b, issue #2351), fuera de PostgreSQL: la
/// decisión de Tenant y rol de <see cref="TenantRlsConnectionInterceptor.DecidirTenantYRol"/>. Con una Sesión
/// Privilegiada seleccionada y un Tenant efectivo distinto del seleccionado, la conexión se queda sin Tenant y
/// conserva <c>cae_app_soporte</c>; nunca queda sin rol (el de login, <c>cae_app_runtime</c>, escribe) ni con el de
/// Aprovisionamiento. Lo que PostgreSQL hace con esa decisión lo demuestra P-6
/// (<c>LineaBaseRlsComunicacionesBajoRuntimeTests</c>), que corre en la CI.
/// </summary>
public class GuardaSesionPrivilegiadaDelInterceptorRlsTests
{
    private const string RolSoporte = "cae_app_soporte";
    private const string RolAprovisionamiento = "cae_app_aprovisionamiento";

    private static readonly Guid Ebro = Guid.NewGuid();
    private static readonly Guid Norte = Guid.NewGuid();
    private static readonly Guid Sesion = Guid.NewGuid();

    [Fact]
    public void Sin_Sesion_Privilegiada_el_Tenant_efectivo_pasa_sin_rol()
    {
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Norte, null, null, ambitoDeAprovisionamiento: false)
            .Should().Be(E(Norte, null));
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Norte, null, Ebro, ambitoDeAprovisionamiento: false)
            .Should().Be(E(Norte, null), "un Delegated Workspace con un ámbito explícito no es una Sesión Privilegiada");
    }

    [Fact]
    public void Con_Sesion_Privilegiada_sobre_su_Tenant_firma_ese_Tenant_con_soporte()
    {
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Ebro, Sesion, Ebro, ambitoDeAprovisionamiento: false)
            .Should().Be(E(Ebro, RolSoporte));
    }

    [Fact]
    public void Con_Sesion_Privilegiada_y_ambito_de_Aprovisionamiento_sobre_su_Tenant_adopta_Aprovisionamiento()
    {
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Ebro, Sesion, Ebro, ambitoDeAprovisionamiento: true)
            .Should().Be(E(Ebro, RolAprovisionamiento));
    }

    [Fact]
    public void Con_Sesion_Privilegiada_y_otro_Tenant_efectivo_queda_sin_Tenant_y_con_soporte()
    {
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Norte, Sesion, Ebro, ambitoDeAprovisionamiento: false)
            .Should().Be(E(null, RolSoporte), "el ámbito sobre Norte no lleva la Sesión Privilegiada sobre Ebro a Norte");
    }

    [Fact]
    public void Con_Sesion_Privilegiada_y_otro_Tenant_efectivo_nunca_adopta_Aprovisionamiento()
    {
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Norte, Sesion, Ebro, ambitoDeAprovisionamiento: true)
            .Should().Be(E(null, RolSoporte), "soporte es el rol más restrictivo; Aprovisionamiento escribe");
    }

    [Fact]
    public void Con_Sesion_Privilegiada_sin_Tenant_seleccionado_no_firma_ningun_Tenant()
    {
        TenantRlsConnectionInterceptor.DecidirTenantYRol(Norte, Sesion, null, ambitoDeAprovisionamiento: false)
            .Should().Be(E(null, RolSoporte));
    }

    /// <summary>La propiedad entera: con una Sesión Privilegiada seleccionada nunca hay rol nulo, y el Tenant firmado
    /// es el seleccionado o ninguno, en todas las combinaciones.</summary>
    [Fact]
    public void Con_Sesion_Privilegiada_nunca_queda_sin_rol_ni_firma_un_Tenant_distinto_del_seleccionado()
    {
        Guid?[] tenants = [null, Ebro, Norte];
        foreach (var efectivo in tenants)
            foreach (var seleccionado in tenants)
                foreach (var aprovisionamiento in new[] { false, true })
                {
                    var (tenant, rol) = TenantRlsConnectionInterceptor.DecidirTenantYRol(
                        efectivo, Sesion, seleccionado, aprovisionamiento);
                    var caso = $"efectivo {efectivo}, seleccionado {seleccionado}, Aprovisionamiento {aprovisionamiento}";

                    rol.Should().NotBeNull($"{caso}: sin rol la conexión se queda con cae_app_runtime, que escribe");
                    if (tenant is not null)
                        tenant.Should().Be(seleccionado, caso);
                    if (efectivo != seleccionado)
                        rol.Should().Be(RolSoporte, caso);
                }
    }

    /// <summary>Con el <see cref="TenantActual"/> de producción: el ámbito explícito sobre Norte manda sobre la
    /// selección de la Sesión Privilegiada sobre Ebro, y la guarda lo corta.</summary>
    [Fact]
    public void Con_el_TenantActual_real_un_ambito_explicito_sobre_otro_Tenant_queda_sin_Tenant()
    {
        var seleccion = new Seleccion(Ebro, Sesion);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, Guid.NewGuid().ToString())], "prueba"));
        var tenantActual = new TenantActual(new Principal(principal), new HttpContextAccessor(), seleccion);

        Decidir(tenantActual, seleccion).Should().Be(E(Ebro, RolSoporte), "barrera: sin ámbito, la sesión fija su Tenant");

        using (AmbitoTenantExplicito.Establecer(Norte))
        {
            tenantActual.TenantId.Should().Be(Norte, "barrera: el ámbito explícito manda en TenantActual");
            Decidir(tenantActual, seleccion).Should().Be(E(null, RolSoporte));
        }

        using (AmbitoTenantExplicito.Establecer(Ebro))
            Decidir(tenantActual, seleccion).Should().Be(E(Ebro, RolSoporte), "un ámbito sobre el propio Tenant de la sesión no cambia nada");
    }

    private static (Guid? TenantId, string? Rol) E(Guid? tenant, string? rol) => (tenant, rol);

    private static (Guid? TenantId, string? Rol) Decidir(ITenantActual tenantActual, IClienteActivoSeleccionado seleccion) =>
        TenantRlsConnectionInterceptor.DecidirTenantYRol(
            tenantActual.TenantId, seleccion.SesionPrivilegiadaIdSeleccionada, seleccion.TenantIdSeleccionado,
            ambitoDeAprovisionamiento: false);

    private sealed class Seleccion(Guid? tenant, Guid? sesionPrivilegiada) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiada;
    }

    private sealed class Principal(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }
}
