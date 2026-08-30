using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// <c>AspNetUsers</c> es la única tabla del sistema sin filtro global de
/// tenant y sin política RLS: el login necesita resolver el usuario, y con él
/// su tenant, antes de conocerlo (ver <c>CaeManagerDbContext</c> y la
/// migración <c>HabilitarRlsPostgres</c>, que la excluye explícitamente). Su
/// aislamiento depende, entera y únicamente, de que el código de aplicación
/// filtre — así que tiene que probarse contra PostgreSQL real, porque no hay
/// ninguna capa por debajo que lo rescate si el filtro falta.
///
/// <para>
/// <c>/roles</c> no filtraba. Los recuentos usaban
/// <c>GetUsersInRoleAsync</c> sobre todos los tenants, la lista de pendientes
/// materializaba <c>UserManager.Users</c> sin filtro —mostrando nombre y correo
/// de empleados de otras organizaciones— y la asignación recuperaba por Guid
/// con <c>FindByIdAsync</c> sin mirar el <c>TenantId</c>, de modo que el Id de
/// un usuario ajeno bastaba para cambiarle el rol.
/// </para>
///
/// <para>
/// La distinción que fija el último test es la que separa <b>ver</b> de
/// <b>mandar</b>: un Operador Delegado es visible desde el tenant que opera
/// (ADR-004 § 5.3), pero su cuenta pertenece a otra organización y su rol se
/// gobierna allí. <c>EsVisibleEnTenantActualAsync</c> diría que sí; toda
/// operación que MODIFIQUE la cuenta tiene que preguntar por la propiedad.
/// </para>
/// </summary>
public class FronteraDeTenantEnGestionDeRolesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualPorAmbito _tenantActual = new();

    private ServiceProvider _servicios = null!;
    private Guid _tenantPropio;
    private Guid _tenantAjeno;
    private Guid _usuarioPropioConRol;
    private Guid _usuarioPropioSinRol;
    private Guid _usuarioAjenoConRol;
    private Guid _usuarioAjenoSinRol;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(_tenantActual);
        servicios.AddScoped<PuertaAccesoDatos>();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddScoped<DirectorioUsuariosTenant>();

        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var propio = new Tenant("Tenant propio");
        var ajeno = new Tenant("Tenant ajeno");
        contexto.Tenants.AddRange(propio, ajeno);
        await contexto.SaveChangesAsync();

        _tenantPropio = propio.Id;
        _tenantAjeno = ajeno.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        _usuarioPropioConRol = await CrearAsync(userManager, "propio-con-rol@x.test", _tenantPropio, Roles.Administrador);
        _usuarioPropioSinRol = await CrearAsync(userManager, "propio-sin-rol@x.test", _tenantPropio, rol: null);
        _usuarioAjenoConRol = await CrearAsync(userManager, "ajeno-con-rol@x.test", _tenantAjeno, Roles.Administrador);
        _usuarioAjenoSinRol = await CrearAsync(userManager, "ajeno-sin-rol@x.test", _tenantAjeno, rol: null);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Los_recuentos_por_rol_solo_cuentan_cuentas_del_tenant_activo()
    {
        // Hay dos Administradores en la base, uno por tenant. Si el recuento
        // dijera 2, estaría contando la cuenta de la otra organización.
        var cantidades = await EjecutarComoAsync(_tenantPropio,
            directorio => directorio.ContarCuentasPropiasPorRolAsync());

        cantidades.GetValueOrDefault(Roles.Administrador).Should().Be(1);
    }

    [Fact]
    public async Task La_lista_de_pendientes_no_expone_cuentas_de_otra_organizacion()
    {
        var pendientes = await EjecutarComoAsync(_tenantPropio,
            directorio => directorio.ObtenerCuentasPropiasSinRolAsync());

        pendientes.Select(u => u.Id).Should().ContainSingle()
            .Which.Should().Be(_usuarioPropioSinRol);

        // Nombre y correo de empleados ajenos era exactamente lo que se
        // filtraba: se nombra el Id concreto para que el fallo diga cuál.
        pendientes.Select(u => u.Id).Should().NotContain(_usuarioAjenoSinRol);
    }

    [Fact]
    public async Task Una_cuenta_de_otro_tenant_no_es_gobernable_desde_este()
    {
        var propia = await EjecutarComoAsync(_tenantPropio,
            d => d.EsCuentaPropiaDelTenantActualAsync(_usuarioPropioConRol));
        var ajena = await EjecutarComoAsync(_tenantPropio,
            d => d.EsCuentaPropiaDelTenantActualAsync(_usuarioAjenoConRol));

        propia.Should().BeTrue();
        ajena.Should().BeFalse(
            "sin esta comprobación, el Id de un usuario ajeno bastaba para cambiarle el rol");
    }

    [Fact]
    public async Task Sin_tenant_resuelto_no_se_ve_nada_en_vez_de_verse_todo()
    {
        // Fallo cerrado: el mismo criterio que el filtro global, donde
        // tenantActual == null produce cero filas y no todas.
        using var ambito = _servicios.CreateScope();
        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();

        (await directorio.ContarCuentasPropiasPorRolAsync()).Should().BeEmpty();
        (await directorio.ObtenerCuentasPropiasSinRolAsync()).Should().BeEmpty();
        (await directorio.EsCuentaPropiaDelTenantActualAsync(_usuarioPropioConRol)).Should().BeFalse();
    }

    private async Task<T> EjecutarComoAsync<T>(Guid tenantId, Func<DirectorioUsuariosTenant, Task<T>> operacion)
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(tenantId);
        using var ambito = _servicios.CreateScope();

        return await operacion(ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>());
    }

    private static async Task<Guid> CrearAsync(
        UserManager<ApplicationUser> userManager, string email, Guid tenantId, string? rol)
    {
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = tenantId,
        };

        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();

        if (rol is not null)
            (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();

        return usuario.Id;
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}
