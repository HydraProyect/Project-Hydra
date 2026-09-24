using System.Reflection;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Queries.VerificarRolAsignable;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using PaginaUsuarios = CaeManager.Web.Features.Usuarios.Pages.Usuarios;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// El alta de /usuarios sella <c>ApplicationUser.TenantId</c> con
/// <c>ITenantActual.TenantId</c>, que es el Context Workspace activo
/// (<c>TenantIdSeleccionado ?? tenant de origen</c>), no el Tenant de origen de
/// quien da el alta. Decisión del propietario (2026-09-23): Administrador y
/// Dirección CAE solo se conceden cuando los dos coinciden
/// (<c>RolesReservadosAlTenantDeOrigen</c>, en Application).
///
/// <para>
/// Escenario: una persona del Operador CAE externo (ArcosSPA) opera el Context
/// Workspace delegado del Tenant propietario (Refrielectric) e intenta dar de
/// alta allí una cuenta con rol Administrador o Dirección CAE. Antes, la cuenta
/// nacía con TenantId del Tenant propietario y superaba
/// <see cref="AutorizacionDelegacionPorAdministradorDelCliente"/>: autoridad
/// de Operación convertida en autoridad de Propiedad (ADR-011 § 1). Ahora el
/// alta se rechaza y no se escribe ninguna cuenta.
/// </para>
///
/// <para>
/// Qué NO prueba: que la persona llegue a la página. <c>[Authorize(Roles =
/// Administrador,DireccionCae)]</c> se evalúa con el rol efectivo que pone
/// <c>RolEfectivoDelWorkspaceMiddleware</c> (el de la Asignación de Cartera), y
/// ni los comandos de runtime ni <c>AsignacionesOperativasWriter</c> conceden
/// ya Administrador ni Dirección CAE. Aquí se instancia la página real sin
/// pasar por esa puerta; la precondición (delegación con rol Administrador) se
/// siembra a mano, como una fila heredada anterior a esa lista blanca.
/// </para>
///
/// <para>
/// Resolución de tenant real: la página recibe <see cref="TenantActual"/> de
/// producción, con el claim firmado <c>tenant_id</c> del Tenant de origen y una
/// selección de Context Workspace — no un <c>ITenantActual</c> fingido.
/// </para>
/// </summary>
public class AltaDeUsuarioDesdeContextWorkspaceDelegadoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private ServiceProvider _servicios = null!;
    private Guid _tenantOperadorCae;      // ArcosSPA: Operador CAE externo, Tenant de origen del actor
    private Guid _tenantPropietario;      // Refrielectric: Tenant propietario que delega
    private Guid _actorDelOperador;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        servicios.AddSingleton<ITenantActual>(new TenantActualPorAmbito());
        servicios.AddScoped<PuertaAccesoDatos>();

        servicios.AddDbContext<CaeManagerDbContext>(opciones => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL")));

        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddDefaultTokenProviders();

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var operador = new Tenant("ArcosSPA (Operador CAE externo)");
        var propietario = new Tenant("Refrielectric (Tenant propietario)");
        contexto.Tenants.AddRange(operador, propietario);
        await contexto.SaveChangesAsync();

        _tenantOperadorCae = operador.Id;
        _tenantPropietario = propietario.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var actor = new ApplicationUser
        {
            UserName = "admin@arcosspa.test",
            Email = "admin@arcosspa.test",
            NombreCompleto = "Administrador de ArcosSPA",
            TenantId = _tenantOperadorCae,
        };
        (await userManager.CreateAsync(actor)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(actor, Roles.Administrador)).Succeeded.Should().BeTrue();
        _actorDelOperador = actor.Id;

        // Precondición sembrada (ver cabecera): delegación viva y cartera con
        // rol Administrador en el Context Workspace del Tenant propietario.
        var delegacion = new DelegacionTenant(_tenantOperadorCae, _tenantPropietario);
        contexto.DelegacionesTenant.Add(delegacion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacion.Id, _actorDelOperador, Roles.Administrador));
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Alta_de_rol_reservado_desde_Context_Workspace_delegado_se_rechaza_y_no_escribe_la_cuenta(string rol)
    {
        var email = $"infiltrado.{rol.ToLowerInvariant()}@arcosspa.test";

        var (nuevo, mensaje) = await DarDeAltaAsync(email, rol, contextWorkspaceSeleccionado: _tenantPropietario);

        nuevo.Should().BeNull(
            "Application rechaza Administrador y Dirección CAE cuando el Context Workspace no es el Tenant de origen del actor");
        mensaje.Should().Contain("solo se asignan desde tu propia organización");

        // Y la autoridad de Propiedad del Tenant propietario no se ha ganado por ninguna vía.
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.Users.AnyAsync(u => u.TenantId == _tenantPropietario)).Should().BeFalse();
    }

    [Fact]
    public async Task Control_positivo_alta_de_Administrador_desde_el_Context_Workspace_propio_nace_en_el_Tenant_de_origen()
    {
        var (nuevo, mensaje) = await DarDeAltaAsync(
            "colega@arcosspa.test", Roles.Administrador, contextWorkspaceSeleccionado: null);

        mensaje.Should().BeNull();
        nuevo.Should().NotBeNull("el alta en el Tenant de origen tiene que escribir la cuenta");
        nuevo!.TenantId.Should().Be(_tenantOperadorCae,
            "sin Context Workspace delegado, TenantActual resuelve al claim tenant_id de origen");

        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        (await userManager.IsInRoleAsync(nuevo, Roles.Administrador)).Should().BeTrue();
        var autorizacion = new AutorizacionDelegacionPorAdministradorDelCliente(userManager);
        (await autorizacion.PuedeGestionarDelegacionesAsync(nuevo.Id, _tenantPropietario)).Should().BeFalse(
            "un Administrador del Operador CAE externo no es Administrador del Tenant propietario");
    }

    [Fact]
    public async Task Control_positivo_un_rol_de_Operacion_se_sigue_dando_de_alta_desde_el_Context_Workspace_delegado()
    {
        // La regla es estrecha: solo Administrador y Dirección CAE. Sin este
        // control, un alta rota por cualquier otro motivo daría verde arriba.
        var (nuevo, mensaje) = await DarDeAltaAsync(
            "gestora@arcosspa.test", Roles.GestorCae, contextWorkspaceSeleccionado: _tenantPropietario);

        mensaje.Should().BeNull();
        nuevo.Should().NotBeNull();
        nuevo!.TenantId.Should().Be(_tenantPropietario);
    }

    private async Task<(ApplicationUser? Nuevo, string? MensajeError)> DarDeAltaAsync(
        string email, string rol, Guid? contextWorkspaceSeleccionado)
    {
        using var ambito = _servicios.CreateScope();
        var sp = ambito.ServiceProvider;

        var autenticacion = new AutenticacionConTenant(_actorDelOperador, _tenantOperadorCae, Roles.Administrador);
        var seleccion = new SeleccionFija(contextWorkspaceSeleccionado);
        var tenantActualReal = new TenantActual(autenticacion, new HttpContextAccessor(), seleccion);

        // Premisa del instrumento: la resolución real devuelve lo que el escenario dice.
        tenantActualReal.TenantId.Should().Be(contextWorkspaceSeleccionado ?? _tenantOperadorCae);

        // MediatR real con los handlers reales de Application, sobre el
        // CurrentUserService y el TenantActual de producción: la regla que se
        // prueba es la de Application, no un doble.
        var serviciosMediator = new ServiceCollection();
        serviciosMediator.AddLogging();
        serviciosMediator.AddSingleton<AuthenticationStateProvider>(autenticacion);
        serviciosMediator.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor());
        serviciosMediator.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        serviciosMediator.AddSingleton<ITenantActual>(tenantActualReal);
        serviciosMediator.AddSingleton<ICurrentUserService, CurrentUserService>();
        serviciosMediator.AddMediatR(cfg => cfg.RegisterServicesFromAssemblyContaining<VerificarRolAsignableQuery>());
        await using var proveedorMediator = serviciosMediator.BuildServiceProvider();

        var pagina = new PaginaUsuarios();
        EscribirPropiedad(pagina, "Mediator", proveedorMediator.GetRequiredService<IMediator>());
        EscribirPropiedad(pagina, "UserManager", sp.GetRequiredService<UserManager<ApplicationUser>>());
        EscribirPropiedad(pagina, "PuertaAccesoDatos", sp.GetRequiredService<PuertaAccesoDatos>());
        EscribirPropiedad(pagina, "TenantActual", tenantActualReal);
        EscribirPropiedad(pagina, "AuthenticationStateProvider", autenticacion);
        EscribirPropiedad(pagina, "ToastService", new ToastService());
        EscribirPropiedad(pagina, "Logger", NullLogger<PaginaUsuarios>.Instance);
        EscribirPropiedad(pagina, "EmailService", new EmailServiceNulo());
        EscribirPropiedad(pagina, "NavigationManager", new NavigationManagerFalsa());

        EscribirCampo(pagina, "_email", email);
        EscribirCampo(pagina, "_nombreCompleto", "Cuenta nueva");
        EscribirCampo(pagina, "_rol", rol);

        await InvocarToleraRecargaSinRendererAsync(pagina, "CrearUsuarioAsync");

        var userManager = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var nuevo = await userManager.FindByEmailAsync(email);
        var mensaje = (string?)LeerCampo(pagina, "_mensajeErrorFormulario");

        return (nuevo, mensaje);
    }

    private static object? LeerCampo(object instancia, string nombre) =>
        (instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo '{nombre}'."))
            .GetValue(instancia);

    private static void EscribirPropiedad(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetProperty(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró la propiedad inyectada '{nombre}'."))
            .SetValue(instancia, valor);

    private static void EscribirCampo(object instancia, string nombre, object? valor) =>
        (instancia.GetType().GetField(nombre, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el campo '{nombre}'."))
            .SetValue(instancia, valor);

    /// <summary>
    /// Tras escribir la cuenta, <c>CrearUsuarioAsync</c> recarga la lista y
    /// llama a <c>StateHasChanged</c>, que sin <c>RenderHandle</c> lanza. Para
    /// entonces la escritura ya ocurrió; lo que se mide viene después.
    /// </summary>
    private static async Task InvocarToleraRecargaSinRendererAsync(object instancia, string metodo)
    {
        var mi = instancia.GetType().GetMethod(metodo, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"No se encontró el método '{metodo}'.");
        try
        {
            await (Task)mi.Invoke(instancia, [])!;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("render handle", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private sealed class SeleccionFija(Guid? tenantIdSeleccionado) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantIdSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class AutenticacionConTenant(Guid usuarioId, Guid tenantOrigen, string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, usuarioId.ToString()),
                new(TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantOrigen.ToString()),
                new(ClaimTypes.Role, rol),
            };
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "prueba"))));
        }
    }

    private sealed class EmailServiceNulo : IEmailService
    {
        public Task<Result> EnviarAsync(string destinatarioEmail, string asunto, string cuerpoHtml, TipoAvisoCorreo tipo, string? responderA = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Exito());
    }

    private sealed class NavigationManagerFalsa : NavigationManager
    {
        public NavigationManagerFalsa() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }
}
