using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearOperadorCaeExterno;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// El alta de un Operador CAE externo con su primer Administrador (FS-22), con el
/// cableado de producción del <see cref="ArnesDeArranqueRuntime"/>: rol
/// <c>cae_app_runtime</c>, los cuatro interceptores, RLS sin debilitar e Identity real
/// con el almacén y el validador de unicidad global de producción.
///
/// <para>
/// Es la capa que garantiza lo que <see cref="CrearOperadorCaeExternoTests"/> no puede
/// ver conectando como propietario de la base: que la cuenta entra por la política de
/// alta de <c>AspNetUsers</c> (<c>TenantId = app.tenant_id</c>) porque la transacción
/// se abre con el Tenant NUEVO como Tenant activo, y que quien da el alta —cuyo
/// Tenant activo es el suyo, el de plataforma— no necesita ver las cuentas de los
/// demás Tenants para que un correo repetido se rechace.
/// </para>
///
/// <para>
/// Quien da el alta actúa desde SU Tenant activo, como llega en producción; el Command
/// abre dentro el ámbito del Tenant que crea.
/// </para>
/// </summary>
public class CrearOperadorCaeExternoBajoRuntimeTests : IAsyncLifetime
{
    private const string EmailAdministrador = "marta@operador-sur.test";

    private readonly ActorMutable _actorAuditoria = new();
    private readonly CurrentUserServiceMutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private Guid _plataforma;
    private Guid _tenantBeneficiario;
    private ApplicationUser _actorPlataforma = null!;
    private ApplicationUser _administradorSinConcesion = null!;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            actorAuditoriaPersonalizado: _actorAuditoria,
            currentUserServicePersonalizado: _sesion,
            serviciosAdicionales: servicios =>
            {
                servicios.AddScoped<PuertaAccesoDatos>();
                servicios.AddScoped<DirectorioUsuariosTenant>();
                // El arranque no genera tokens; el alta de una cuenta sí.
                new IdentityBuilder(typeof(ApplicationUser), typeof(IdentityRole<Guid>), servicios).AddDefaultTokenProviders();
            });

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            _plataforma = (await propietarioDeLaBase.Tenants.SingleAsync(t => t.EsPlataforma)).Id;
            var beneficiario = new Tenant($"Tenant beneficiario {Guid.NewGuid():N}", PerfilVocabularioTenant.ClienteDirecto);
            propietarioDeLaBase.Tenants.Add(beneficiario);
            await propietarioDeLaBase.SaveChangesAsync();
            _tenantBeneficiario = beneficiario.Id;
        }

        _actorPlataforma = await SembrarUsuarioAsync("actor-plataforma", _plataforma, Roles.Administrador);
        _administradorSinConcesion = await SembrarUsuarioAsync("administrador-sin-concesion", _plataforma, Roles.Administrador);
        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            propietarioDeLaBase.ConcesionesPrivilegio.Add(ConcesionPrivilegio.Global(
                _actorPlataforma.Id, vigenciaDesde: DateTime.UtcNow.AddMinutes(-5), vigenciaHasta: null));
            await propietarioDeLaBase.SaveChangesAsync();
        }
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task El_Actor_de_Plataforma_crea_el_Operador_y_su_primer_Administrador_entra_por_la_politica_del_Tenant_nuevo()
    {
        var resultado = await EjecutarAsync(_actorPlataforma, EmailAdministrador);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : string.Empty);
        var creado = resultado.Valor;

        await using (var propietarioDeLaBase = ContextoPropietarioDeLaBase())
        {
            var tenant = await propietarioDeLaBase.Tenants.SingleAsync(t => t.Id == creado.TenantId);
            tenant.PuedeActuarComoOperadorCaeExterno.Should().BeTrue();
            (await propietarioDeLaBase.ParametrosSistema.IgnoreQueryFilters().CountAsync(p => p.TenantId == creado.TenantId))
                .Should().Be(1);
            (await propietarioDeLaBase.AsignacionesOperacion.IgnoreQueryFilters()
                    .CountAsync(o => o.EsRaiz && o.PropietarioTenantId == creado.TenantId))
                .Should().Be(1);

            var cuenta = await propietarioDeLaBase.Users.SingleAsync(u => u.Id == creado.PrimerAdministradorUsuarioId);
            cuenta.TenantId.Should().Be(creado.TenantId);
            cuenta.TenantId.Should().NotBe(_plataforma, "no nace en el Tenant de quien da el alta");
            cuenta.Email.Should().Be(EmailAdministrador);
            cuenta.PasswordHash.Should().BeNull();

            var roles = await (from afiliacion in propietarioDeLaBase.UserRoles
                               join rol in propietarioDeLaBase.Roles on afiliacion.RoleId equals rol.Id
                               where afiliacion.UserId == cuenta.Id
                               select rol.Name).ToListAsync();
            roles.Should().Equal([Roles.Administrador]);

            (await propietarioDeLaBase.Users.CountAsync(u => u.TenantId == _plataforma))
                .Should().Be(2, "en el Tenant de plataforma siguen solo las dos cuentas sembradas");
        }

        // El token vale para esa cuenta, leída ya como la leería ella: bajo RLS, en su Tenant.
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        _sesion.UsuarioId = null;
        _sesion.TenantOrigenId = null;
        using (AmbitoTenantExplicito.Establecer(creado.TenantId))
        {
            var cuenta = await userManager.FindByIdAsync(creado.PrimerAdministradorUsuarioId.ToString());
            cuenta.Should().NotBeNull();
            var token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(creado.TokenActivacion));
            (await userManager.VerifyUserTokenAsync(
                    cuenta!, userManager.Options.Tokens.PasswordResetTokenProvider,
                    UserManager<ApplicationUser>.ResetPasswordTokenPurpose, token))
                .Should().BeTrue();
        }
    }

    /// <summary>
    /// La cuenta que ya usa ese correo es de OTRO Tenant: quien da el alta no la ve (RLS de
    /// <c>AspNetUsers</c>), y el Command tampoco desde el ámbito del Tenant nuevo. Aun así
    /// el alta falla, con el mensaje neutro, y no queda el Tenant.
    /// </summary>
    [Fact]
    public async Task Un_correo_que_ya_usa_una_cuenta_invisible_de_otro_Tenant_falla_y_no_deja_el_Tenant()
    {
        var ajena = await SembrarUsuarioAsync("gestor-de-otro-tenant", _tenantBeneficiario, Roles.GestorCae);
        var (tenantsAntes, cuentasAntes) = await ContarAsync();

        var resultado = await EjecutarAsync(_actorPlataforma, ajena.Email!);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(CrearOperadorCaeExternoCommandHandler.PrimerAdministradorNoCreado);
        (await ContarAsync()).Should().Be((tenantsAntes, cuentasAntes), "ni el Tenant ni ninguna cuenta nueva");

        // Control positivo, con otro correo y el mismo actor: lo que impidió el alta fue el
        // correo, no el arnés ni la autorización.
        var conOtroCorreo = await EjecutarAsync(_actorPlataforma, EmailAdministrador);
        conOtroCorreo.EsExitoso.Should().BeTrue(conOtroCorreo.EsFallido ? conOtroCorreo.Error.Mensaje : string.Empty);
    }

    [Fact]
    public async Task Sin_concesion_de_plataforma_no_se_crea_ni_el_Tenant_ni_la_cuenta()
    {
        var (tenantsAntes, cuentasAntes) = await ContarAsync();

        var resultado = await EjecutarAsync(_administradorSinConcesion, EmailAdministrador);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("OperadorCaeExterno.SinPermiso");
        (await ContarAsync()).Should().Be((tenantsAntes, cuentasAntes));
    }

    // ── Montaje ──────────────────────────────────────────────────────────────

    private async Task<Result<OperadorCaeExternoCreado>> EjecutarAsync(ApplicationUser actor, string emailAdministrador)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var servicios = ambito.ServiceProvider;
        var contexto = servicios.GetRequiredService<CaeManagerDbContext>();
        var usuarioActual = new CurrentUserServiceFalso(actor.Id, tenantOrigenId: actor.TenantId);
        _actorAuditoria.Actual = actor.Id;
        _sesion.UsuarioId = actor.Id;
        _sesion.TenantOrigenId = actor.TenantId;

        var handler = new CrearOperadorCaeExternoCommandHandler(
            new TenantRepository(contexto),
            new ParametroSistemaRepository(contexto),
            new AutorizacionAdminPlataformaPorConcesion(contexto),
            usuarioActual,
            new AsignacionesOperativasWriter(contexto, servicios.GetRequiredService<ITenantActual>(), usuarioActual),
            contexto,
            new GestionCuentasUsuarioIdentity(
                servicios.GetRequiredService<UserManager<ApplicationUser>>(),
                servicios.GetRequiredService<PuertaAccesoDatos>(),
                servicios.GetRequiredService<DirectorioUsuariosTenant>(),
                contexto),
            new TransaccionDeComando(contexto),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<CrearOperadorCaeExternoCommandHandler>.Instance);

        // Tenant activo = el de quien da el alta, como en producción.
        using (AmbitoTenantExplicito.Establecer(actor.TenantId))
            return await handler.Handle(
                new CrearOperadorCaeExternoCommand($"Operador Sur {Guid.NewGuid():N}", emailAdministrador, "Marta Ruiz"),
                CancellationToken.None);
    }

    private async Task<(int Tenants, int Cuentas)> ContarAsync()
    {
        await using var propietarioDeLaBase = ContextoPropietarioDeLaBase();
        return (await propietarioDeLaBase.Tenants.CountAsync(), await propietarioDeLaBase.Users.CountAsync());
    }

    private async Task<ApplicationUser> SembrarUsuarioAsync(string alias, Guid tenant, string rol)
    {
        using var ambito = _arnes.Servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{alias}-{Guid.NewGuid():N}@talveg.test";
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = alias,
            EmailConfirmed = true,
            TenantId = tenant,
        };

        using (AmbitoTenantExplicito.Establecer(tenant))
        {
            (await userManager.CreateAsync(usuario, "Arnes#2026Seguro")).Succeeded.Should().BeTrue();
            (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();
        }

        return usuario;
    }

    /// <summary>
    /// Rol propietario de la base, sin interceptores: solo siembra y comprueba. Lo
    /// que se prueba corre siempre por el arnés, como <c>cae_app_runtime</c>.
    /// </summary>
    private CaeManagerDbContext ContextoPropietarioDeLaBase()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario)
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), new SinTenant());
    }

    private sealed class SinTenant : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class ActorMutable : IActorAuditoria
    {
        public Guid? Actual { get; set; }
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(Actual));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(Actual);
    }
}
