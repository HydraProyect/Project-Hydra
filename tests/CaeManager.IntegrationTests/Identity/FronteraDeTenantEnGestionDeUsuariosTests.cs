using System.Reflection;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PaginaUsuarios = CaeManager.Web.Features.Usuarios.Pages.Usuarios;
using UsuarioListaDto = CaeManager.Web.Features.Usuarios.Pages.UsuarioListaDto;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// <c>AspNetUsers</c> es la única tabla del sistema sin filtro global de
/// tenant y sin política RLS (ver <c>FronteraDeTenantEnGestionDeRolesTests</c>,
/// que cerró el mismo hueco en <c>/roles</c>): su aislamiento depende, entera
/// y únicamente, de que el código de aplicación filtre.
///
/// <para>
/// <c>/usuarios</c> tenía el mismo hueco que tenía <c>/roles</c> antes de su
/// fix, sin haberlo heredado de ahí — es una recurrencia independiente del
/// mismo error, no una regresión. <c>AbrirEditarAsync</c>,
/// <c>EditarUsuarioAsync</c> y <c>CambiarActivacionAsync</c> recuperaban la
/// cuenta con <c>UserManager.FindByIdAsync(id)</c> sin preguntar nunca por
/// <c>DirectorioUsuariosTenant.EsCuentaPropiaDelTenantActualAsync</c> — el
/// mismo predicado que <c>/roles</c> ya usa y que estos tests reutilizan tal
/// cual, sin fingir un tenant ni debilitar el filtro que lo implementa.
/// </para>
///
/// <para>
/// El vector es más directo que en <c>/roles</c>: un Operador Delegado
/// aparece en <c>/usuarios</c> del tenant que opera, marcado "Delegado" (ADR-004
/// § 5.3, ver <c>DirectorioUsuariosTenant.ObtenerVisiblesAsync</c>), con menú
/// de "Editar" y "Desactivar/Reactivar" en la misma fila que cualquier cuenta
/// propia — no hace falta adivinar un Guid ajeno, el Id de ataque es el de una
/// fila real y visible.
/// </para>
///
/// <para>
/// Por qué no bUnit: <c>UsuariosGen2Tests</c> sustituye las cuatro lecturas
/// del directorio por dobles y la escritura por un <c>UserManagerFalso</c> sin
/// concepto de tenant — no podría distinguir una cuenta propia de una ajena
/// aunque el guardián faltara. Aquí se instancia la página real (sin bUnit,
/// sin renderer: los tres métodos bajo prueba no llaman a
/// <c>StateHasChanged</c>) contra <c>UserManager</c>, <c>PuertaAccesoDatos</c>
/// y <c>DirectorioUsuariosTenant</c> reales sobre PostgreSQL — la única
/// combinación que puede fallar por la razón real.
/// </para>
/// </summary>
public class FronteraDeTenantEnGestionDeUsuariosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly TenantActualPorAmbito _tenantActual = new();

    private ServiceProvider _servicios = null!;
    private Guid _tenantPropio;
    private Guid _tenantAjeno;
    private Guid _actorAdministrador;
    private Guid _usuarioAjenoDelegado;

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
        var ajeno = new Tenant("Consultora externa");
        contexto.Tenants.AddRange(propio, ajeno);
        await contexto.SaveChangesAsync();

        _tenantPropio = propio.Id;
        _tenantAjeno = ajeno.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        _actorAdministrador = await CrearAsync(userManager, "admin-propio@x.test", _tenantPropio, Roles.Administrador);
        _usuarioAjenoDelegado = await CrearAsync(userManager, "gestor-ajeno@x.test", _tenantAjeno, Roles.GestorCae);

        // Delegación viva de la consultora (tenant ajeno) sobre el tenant
        // propio: es lo que hace que la fila del gestor ajeno aparezca en
        // /usuarios del tenant propio marcada "Delegado" — el vector real,
        // no un Id inventado.
        var delegacion = new DelegacionTenant(_tenantAjeno, _tenantPropio);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegado.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _usuarioAjenoDelegado, Roles.GestorCae));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_gestor_ajeno_delegado_es_visible_en_el_tenant_propio()
    {
        // Confirma la premisa del ataque antes de probar la defensa: el Id no
        // se inventa, sale de una fila real que /usuarios del tenant propio
        // muestra hoy con la marca "Delegado".
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantPropio);
        using var ambito = _servicios.CreateScope();
        var directorio = ambito.ServiceProvider.GetRequiredService<DirectorioUsuariosTenant>();

        (await directorio.ObtenerVisiblesAsync()).Select(u => u.Id).Should().Contain(_usuarioAjenoDelegado);
        (await directorio.ObtenerRolesDeOperadoresDelegadosAsync()).Should().ContainKey(_usuarioAjenoDelegado);
    }

    [Fact]
    public async Task AbrirEditarAsync_rechaza_la_ficha_de_un_operador_delegado()
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantPropio);
        using var ambito = _servicios.CreateScope();
        var pagina = CrearPagina(ambito.ServiceProvider, _actorAdministrador, esAdministrador: true);

        await InvocarAsync(pagina, "AbrirEditarAsync", _usuarioAjenoDelegado);

        LeerCampoPrivado<bool>(pagina, "_drawerVisible").Should().BeFalse(
            "el Id del gestor ajeno viene de una fila real (\"Delegado\") de esta lista, y su ficha se gobierna en su propio tenant");
        LeerCampoPrivado<Guid?>(pagina, "_editandoId").Should().BeNull();
    }

    [Fact]
    public async Task GuardarAsync_no_edita_la_ficha_de_un_operador_delegado()
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantPropio);
        using var ambito = _servicios.CreateScope();
        var pagina = CrearPagina(ambito.ServiceProvider, _actorAdministrador, esAdministrador: true);

        EscribirCampoPrivado(pagina, "_nombreCompleto", "Nombre Suplantado");
        EscribirCampoPrivado(pagina, "_rol", Roles.Administrador);
        EscribirCampoPrivado(pagina, "_usuarioActualEsAdministrador", true);
        EscribirCampoPrivado(pagina, "_usuarioActualId", (Guid?)_actorAdministrador);

        await InvocarToleraRecargaSinRendererAsync(pagina, "EditarUsuarioAsync", _usuarioAjenoDelegado);

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuarioTrasElAtaque = await userManager.FindByIdAsync(_usuarioAjenoDelegado.ToString());

        usuarioTrasElAtaque!.NombreCompleto.Should().Be("gestor-ajeno@x.test",
            "editar la ficha de un Operador Delegado desde aquí escribiría sobre la identidad de otra organización");
        (await userManager.GetRolesAsync(usuarioTrasElAtaque)).Should().BeEquivalentTo(
            [Roles.GestorCae],
            because: "el rol de un Operador Delegado se gobierna en su propio tenant, nunca desde el que lo ve operar");
    }

    [Fact]
    public async Task CambiarActivacionAsync_no_desactiva_la_cuenta_de_un_operador_delegado()
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantPropio);
        using var ambito = _servicios.CreateScope();
        var pagina = CrearPagina(ambito.ServiceProvider, _actorAdministrador, esAdministrador: true);

        var filaDelegada = new UsuarioListaDto(
            _usuarioAjenoDelegado, "gestor-ajeno@x.test", "Gestor Ajeno", Roles.GestorCae,
            Activo: true, EsOperadorDelegado: true, Alcance: null!);

        await InvocarToleraRecargaSinRendererAsync(pagina, "CambiarActivacionAsync", filaDelegada);

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuarioTrasElAtaque = await userManager.FindByIdAsync(_usuarioAjenoDelegado.ToString());

        usuarioTrasElAtaque!.LockoutEnd.Should().BeNull(
            "desactivar la cuenta de un Operador Delegado desde el tenant que opera sería una denegación de servicio contra otra organización");
    }

    private static PaginaUsuarios CrearPagina(IServiceProvider servicios, Guid actorId, bool esAdministrador)
    {
        var pagina = new PaginaUsuarios();

        EscribirPropiedadInyectada(pagina, "UserManager", servicios.GetRequiredService<UserManager<ApplicationUser>>());
        EscribirPropiedadInyectada(pagina, "PuertaAccesoDatos", servicios.GetRequiredService<PuertaAccesoDatos>());
        EscribirPropiedadInyectada(pagina, "DirectorioUsuarios", servicios.GetRequiredService<DirectorioUsuariosTenant>());
        EscribirPropiedadInyectada(pagina, "ToastService", new ToastService());
        EscribirPropiedadInyectada(pagina, "Logger", NullLogger<PaginaUsuarios>.Instance);
        EscribirPropiedadInyectada(pagina, "AuthenticationStateProvider", new AutenticacionFalsa(actorId, esAdministrador));

        return pagina;
    }

    private static async Task<Guid> CrearAsync(
        UserManager<ApplicationUser> userManager, string email, Guid tenantId, string rol)
    {
        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = tenantId,
        };

        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(usuario, rol)).Succeeded.Should().BeTrue();

        return usuario.Id;
    }

    // ── Reflexión: los tres métodos bajo prueba son privados en la página, a
    // propósito (no exponen ninguna API que no sea el propio marcado Razor).
    // Invocarlos así ejercita el código de producción tal cual, sin bUnit y
    // sin necesitar un RenderHandle real — ninguno de los tres llama a
    // StateHasChanged.

    private static void EscribirPropiedadInyectada(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad inyectada '{nombre}'."))
            .SetValue(instancia, valor);

    private static void EscribirCampoPrivado(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo '{nombre}'."))
            .SetValue(instancia, valor);

    private static T LeerCampoPrivado<T>(object instancia, string nombre) =>
        (T)(instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo '{nombre}'."))
            .GetValue(instancia)!;

    private static async Task InvocarAsync(object instancia, string metodo, params object?[] argumentos)
    {
        var mi = instancia.GetType().GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el método '{metodo}'.");
        await (Task)mi.Invoke(instancia, argumentos)!;
    }

    /// <summary>
    /// Si el guardián bajo prueba falta (mutación, o el hallazgo reaparece), el
    /// camino de ÉXITO de la operación llega a <c>CargarAsync</c>, que llama a
    /// <c>StateHasChanged</c>: sin un <c>RenderHandle</c> real —la página no se
    /// renderiza aquí a propósito, ver la cabecera de esta clase— esa llamada
    /// lanza <see cref="InvalidOperationException"/>. Es una limitación del
    /// arnés, no la propiedad bajo prueba: para cuando lanza, la escritura
    /// sobre la cuenta ajena ya ocurrió, y son las aserciones posteriores a
    /// esta llamada las que tienen que fallar, no esta excepción.
    /// </summary>
    private static async Task InvocarToleraRecargaSinRendererAsync(object instancia, string metodo, params object?[] argumentos)
    {
        try
        {
            await InvocarAsync(instancia, metodo, argumentos);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("render handle", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class AutenticacionFalsa(Guid usuarioId, bool esAdministrador) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, usuarioId.ToString()) };
            if (esAdministrador)
                claims.Add(new Claim(ClaimTypes.Role, Roles.Administrador));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba"));
            return Task.FromResult(new AuthenticationState(principal));
        }
    }
}
