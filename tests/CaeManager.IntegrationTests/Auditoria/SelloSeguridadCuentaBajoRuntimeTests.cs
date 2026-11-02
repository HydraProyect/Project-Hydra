using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
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
    public async Task Lee_el_sello_de_la_base_aunque_la_peticion_tenga_rastreada_una_entidad_anterior()
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

        var vigente = await ambito.ServiceProvider.GetRequiredService<ISelloSeguridadCuenta>().VigenteAsync(cuenta, TenantA);

        vigente.Should().NotBeNullOrEmpty().And.NotBe(selloAlCargar);
        rastreada.SecurityStamp.Should().Be(selloAlCargar, "control: la entidad rastreada no se ha enterado");
    }

    [Fact]
    public async Task Fuera_de_su_Tenant_propietario_la_cuenta_no_se_encuentra()
    {
        var cuenta = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(cuenta);
        await CrearCuentaAsync(arnes, cuenta);

        using var ambito = arnes.Servicios.CreateScope();
        var sellos = ambito.ServiceProvider.GetRequiredService<ISelloSeguridadCuenta>();

        (await sellos.VigenteAsync(cuenta, TenantA)).Should().NotBeNull("control: en su Tenant sí se lee");
        (await sellos.VigenteAsync(cuenta, Guid.NewGuid())).Should().BeNull("la RLS no se abre por leer un sello");
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
        (await sellos.VigenteAsync(cuenta, TenantA)).Should().Be(usuario.SecurityStamp);
    }

    // ---------- Arnés ----------

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(Guid cuenta) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(ActorAuditoria.Normal(cuenta)),
            currentUserServicePersonalizado: new UsuarioFijo(cuenta, TenantA),
            serviciosAdicionales: s => s.AddScoped<ISelloSeguridadCuenta, SelloSeguridadCuenta>());

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

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantId;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class UsuarioFijo(Guid usuarioId, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Consulta");
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>("Consulta");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }
}
