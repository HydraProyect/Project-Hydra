using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// <see cref="SelloSeguridadCuenta"/> con una cuenta real de Identity, el almacén real y la identidad de la
/// aplicación (<c>cae_app_runtime</c>): la lectura pasa la RLS de <c>AspNetUsers</c> con el Tenant propietario
/// aunque el contexto traiga otro, como en un refresco, y una rotación de Identity llega a la anotación.
/// </summary>
public class SelloSeguridadCuentaBajoRuntimeTests
{
    private static readonly Guid TenantA = TenantSeedData.IdPorDefecto;

    [Fact]
    public async Task Compara_con_la_base_aunque_la_peticion_tenga_rastreada_una_entidad_anterior()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);

        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var rastreada = await um.FindByIdAsync(cuenta.ToString());
        var selloAlCargar = rastreada!.SecurityStamp;

        using (var otraPeticion = arnes.Servicios.CreateScope())
        {
            var otroUm = otraPeticion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var otra = await otroUm.FindByIdAsync(cuenta.ToString());
            (await otroUm.UpdateSecurityStampAsync(otra!)).Succeeded.Should().BeTrue();
        }

        var vigenteEnLaOtraPeticion = await SelloEnLaBaseAsync(arnes, cuenta);
        var sellos = ambito.ServiceProvider.GetRequiredService<ISelloSeguridadCuenta>();

        (await sellos.EsVigenteAsync(cuenta, TenantA, selloAlCargar)).Should().BeFalse();
        (await sellos.EsVigenteAsync(cuenta, TenantA, vigenteEnLaOtraPeticion)).Should().BeTrue();
        rastreada.SecurityStamp.Should().Be(selloAlCargar, "control: la entidad rastreada no se ha enterado");
    }

    /// <summary>
    /// La cuenta actual es otra para que no entre por la rama de «la propia cuenta» de la política. El filtro
    /// global de EF ya no deja pasar la cuenta en otro Tenant, así que la última comprobación lo quita: lo que
    /// queda es la RLS con la identidad de la aplicación.
    /// </summary>
    [Fact]
    public async Task Fuera_de_su_Tenant_propietario_la_RLS_no_deja_ver_la_cuenta()
    {
        var cuenta = Guid.NewGuid();
        var otraCuenta = Guid.NewGuid();
        var otroTenant = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(otraCuenta);
        await CrearCuentaAsync(arnes, cuenta);
        var sello = await SelloEnLaBaseAsync(arnes, cuenta);

        using var ambito = arnes.Servicios.CreateScope();
        var sellos = ambito.ServiceProvider.GetRequiredService<ISelloSeguridadCuenta>();
        (await sellos.EsVigenteAsync(cuenta, TenantA, sello)).Should().BeTrue("control: en su Tenant sí coincide");
        (await sellos.EsVigenteAsync(cuenta, otroTenant, sello)).Should().BeFalse("la RLS no se abre por comparar un sello");

        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        using (AmbitoTenantExplicito.Establecer(TenantA))
            (await contexto.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u => u.Id == cuenta)).Should().BeTrue("control sin el filtro de EF");
        using (AmbitoTenantExplicito.Establecer(otroTenant))
            (await contexto.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync(u => u.Id == cuenta)).Should().BeFalse("sin el filtro de EF, solo queda la RLS");
    }

    /// <summary>
    /// Como al iniciar sesión y en el ámbito hijo de un circuito: ni cuenta firmada en el contexto ni Tenant
    /// actual. La comparación se apoya solo en el Tenant propietario que recibe.
    /// </summary>
    [Fact]
    public async Task Sin_usuario_ni_Tenant_en_el_contexto_compara_igual()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta, usuarioActual: null, tenantActual: null);
        await CrearCuentaAsync(arnes, cuenta);
        var sello = await SelloEnLaBaseAsync(arnes, cuenta);

        using var ambito = arnes.Servicios.CreateScope();
        var sellos = ambito.ServiceProvider.GetRequiredService<ISelloSeguridadCuenta>();

        (await sellos.EsVigenteAsync(cuenta, TenantA, sello)).Should().BeTrue();
        (await sellos.EsVigenteAsync(cuenta, TenantA, "otro-sello")).Should().BeFalse();
    }

    [Fact]
    public async Task Una_rotacion_de_Identity_queda_anotada_con_el_sello_que_sustituye()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);

        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sellos = ambito.ServiceProvider.GetRequiredService<ISelloSeguridadCuenta>();
        var usuario = await um.FindByIdAsync(cuenta.ToString());
        var anteriorEsperado = usuario!.SecurityStamp;

        (await um.UpdateSecurityStampAsync(usuario)).Succeeded.Should().BeTrue();

        sellos.RotadoEnEstaPeticion(cuenta, out var anterior).Should().BeTrue();
        anterior.Should().Be(anteriorEsperado);
        (await sellos.EsVigenteAsync(cuenta, TenantA, usuario.SecurityStamp)).Should().BeTrue();
    }

    // ---------- Arnés ----------

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid cuenta) =>
        CrearArnesAsync(cuenta, usuarioActual: cuenta, tenantActual: TenantA);

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid actor, Guid? usuarioActual, Guid? tenantActual) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(tenantActual),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(actor)),
            currentUserServicePersonalizado: new UsuarioFijo(usuarioActual, tenantActual),
            serviciosAdicionales: s => s.AddScoped<ISelloSeguridadCuenta, SelloSeguridadCuenta>());

    private static async Task<string> SelloEnLaBaseAsync(ArnesDeArranqueRuntime arnes, Guid cuenta)
    {
        using var ambito = arnes.Servicios.CreateScope();
        using (AmbitoTenantExplicito.Establecer(TenantA))
        {
            var usuario = await ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(cuenta.ToString());
            return usuario!.SecurityStamp!;
        }
    }

    private static async Task CrearCuentaAsync(ArnesDeArranqueRuntime arnes, Guid id)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"sello-{id:N}@talveg.test";
        var usuario = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            NombreCompleto = email,
            EmailConfirmed = true,
            TenantId = TenantA,
        };

        // La preparación no es lo que se mide: su ámbito explícito, como en SegundoFactorBajoRuntimeTests.
        using (AmbitoTenantExplicito.Establecer(TenantA))
        {
            var resultado = await um.CreateAsync(usuario, "Arnes#2026Seguro");
            resultado.Succeeded.Should().BeTrue(string.Join(", ", resultado.Errors.Select(e => e.Code)));
        }
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantId;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class UsuarioFijo(Guid? usuarioId, Guid? tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Consulta");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Consulta");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }
}
