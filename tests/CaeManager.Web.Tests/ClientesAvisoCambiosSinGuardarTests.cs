using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Clientes.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// S12 (lote 2b), piloto Clientes: el alta y la edición de Cliente empresarial usan <c>DrawerFormulario</c>. Salir de /clientes con
/// el drawer a medias pregunta una sola vez; «Continuar con la empresa» no pregunta (lo escrito ya está guardado); «Cancelar»
/// pregunta como la X (D-05). El modal de guardar filtro usa <c>ModalFormulario</c> (S12, lote 3a) y también pregunta
/// una sola vez. Antes de migrar la página no tenía ninguno de estos casos probado a nivel de componente (solo E2E).
///
/// <para>
/// Arnés copiado de <c>ClientesVacioPorFiltroTests</c> (la página inyecta el directorio de usuarios, una clase concreta, y el
/// proveedor de autenticación): sin tenant resuelto el directorio devuelve vacío sin tocar la base.
/// </para>
/// </summary>
public class ClientesAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly Guid ClienteCreadoId = Guid.NewGuid();

    public ClientesAvisoCambiosSinGuardarTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerFiltrosGuardadosQuery => (object)Array.Empty<FiltroGuardadoDto>(),
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[new(Guid.NewGuid(), "Propia", EsOrigen: true)],
                CrearClienteCommand => Result.Exito(ClienteCreadoId),
                ObtenerClientesQuery q => new ResultadoPaginado<ClienteListaDto>([], 0, q.Pagina, q.TamanoPagina),
                ObtenerAlcanceCeroQuery => (object)false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Sin tenant a propósito: el directorio de usuarios devuelve vacío sin consultar nada.</summary>
    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

    private sealed class TenantsQueryContextQueNadieDebeTocar : ITenantsQueryContext
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Sin tenant resuelto el directorio devuelve vacío sin consultar; si esto salta, cambió el camino.");

        public IQueryable<Tenant> Tenants => throw NoDeberia();
        public IQueryable<DelegacionTenant> DelegacionesTenant => throw NoDeberia();
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => throw NoDeberia();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw NoDeberia();
    }

    private sealed class AlmacenUsuariosQueNadieDebeTocar : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Sin tenant resuelto no se consulta ningún usuario; si esto salta, cambió el camino.");

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public void Dispose() { }
    }

    /// <summary>
    /// bUnit registra un <c>IAuthorizationService</c> que lanza si no se usa su propio helper: este evalúa los roles de verdad
    /// contra el principal (Administrador, el más permisivo).
    /// </summary>
    private sealed class AutorizacionPorRoles : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements)
        {
            var cumple = requirements.All(r => r switch
            {
                RolesAuthorizationRequirement roles => roles.AllowedRoles.Any(user.IsInRole),
                DenyAnonymousAuthorizationRequirement => user.Identity?.IsAuthenticated == true,
                _ => true
            });

            return Task.FromResult(cumple ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        }

        public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class AutenticacionFalsa : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, Roles.Administrador)], "test"))));
    }

    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(ClientesAvisoCambiosSinGuardarTests)),
            tenantActual);

        var userManager = new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!);

        return new DirectorioUsuariosTenant(
            userManager, new TenantsQueryContextQueNadieDebeTocar(), tenantActual,
            new PuertaAccesoDatos(), identidad);
    }

    private IRenderedComponent<Clientes> Renderizar(string accion)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo());
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<AuthenticationStateProvider, AutenticacionFalsa>();
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped(_ => CrearDirectorio());
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddScoped<PuertaAccesoDatos>();

        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes?accion=" + accion);
        var cut = Render<Clientes>();
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel, .modal-contenido").Should().NotBeEmpty());
        return cut;
    }

    private static Task EscribirCifAsync(IRenderedComponent<Clientes> cut, string cif) =>
        cut.FindAll(".drawer-panel input").First(i => i.GetAttribute("placeholder") == "CIF, DNI o NIE")
            .InputAsync(new ChangeEventArgs { Value = cif });

    private static int PreguntasDeSalida(IRenderedComponent<Clientes> cut) =>
        cut.FindAll(".modal-pie button").Count(b => b.TextContent.Trim() == "Salir y descartar");

    [Fact]
    public async Task Salir_con_el_alta_a_medias_pregunta_una_vez_y_deja_seguir_editando()
    {
        var cut = Renderizar("crear");
        await EscribirCifAsync(cut, "B12345678");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        navegacion.Uri.Should().Be(origen, "con el alta a medias la navegación se detiene");
        PreguntasDeSalida(cut).Should().Be(1, "una navegación, una pregunta");
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());

        PreguntasDeSalida(cut).Should().Be(0);
        cut.FindComponents<CampoTexto>().Should().Contain(c => c.Instance.Valor == "B12345678");
    }

    [Fact]
    public async Task Salir_y_descartar_navega_al_destino()
    {
        var cut = Renderizar("crear");
        await EscribirCifAsync(cut, "B12345678");
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));
        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Salir y descartar").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().EndWith("/trabajadores");
        PreguntasDeSalida(cut).Should().Be(0, "descartar no vuelve a preguntar");

        // «Salir y descartar» cierra el drawer por el mismo camino que «Cancelar» (el kit llama a VisibleChanged(false)), que quita
        // ?accion= de la URL con un reemplazo antes de la navegación confirmada. Fijado porque antes no ocurría (el AlDescartar de
        // la página solo cerraba): primero el reemplazo sin ?accion= y después, una sola vez, el destino. Medido solo con el
        // arnés de bUnit, no con el circuito real.
        var historial = ((BunitNavigationManager)navegacion).History.ToList(); // el más reciente primero
        historial[0].Uri.Should().EndWith("/trabajadores");
        historial.Count(h => h.Uri.EndsWith("/trabajadores")).Should().Be(1, "una sola navegación al destino");
        historial[1].Uri.Should().EndWith("/clientes", "el reemplazo que quita la acción precede al destino");
        historial[1].Options.ReplaceHistoryEntry.Should().BeTrue();
    }

    [Fact]
    public async Task Continuar_con_la_empresa_tras_guardar_no_pregunta()
    {
        var cut = Renderizar("crear");
        await EscribirCifAsync(cut, "B12345678");
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Continuar con la empresa").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().Contain($"/empresas?accion=crear&clienteId={ClienteCreadoId}",
            "lo escrito ya está guardado: la navegación del propio formulario no pregunta");
        PreguntasDeSalida(cut).Should().Be(0);
    }

    [Fact]
    public async Task Cancelar_con_cambios_pregunta_como_la_X_y_no_es_una_navegacion()
    {
        var cut = Renderizar("crear");
        await EscribirCifAsync(cut, "B12345678");

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
        PreguntasDeSalida(cut).Should().Be(0, "cerrar el drawer no es navegar");
        cut.FindAll(".drawer-panel").Should().NotBeEmpty();
    }

    [Fact]
    public async Task El_nombre_escrito_en_guardar_filtro_pregunta_al_salir_una_sola_vez()
    {
        var cut = Renderizar("guardar-filtro");
        await cut.FindAll(".modal-contenido input").First().InputAsync(new ChangeEventArgs { Value = "Críticos" });
        var navegacion = Services.GetRequiredService<NavigationManager>();
        var origen = navegacion.Uri;

        await cut.InvokeAsync(() => navegacion.NavigateTo("/trabajadores"));

        navegacion.Uri.Should().Be(origen);
        PreguntasDeSalida(cut).Should().Be(1);
    }

    // ------------------------------------------------------------- Piloto de ModalFormulario: «Guardar filtro» (S12, lote 3a)

    [Fact]
    public async Task Guardar_filtro_sin_nombre_esta_deshabilitado_y_dice_por_que_y_con_nombre_se_habilita()
    {
        var cut = Renderizar("guardar-filtro");
        var guardar = () => cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar");

        guardar().HasAttribute("disabled").Should().BeTrue("sin nombre no hay nada que guardar");
        guardar().GetAttribute("title").Should().Be("Escribe un nombre para el filtro", "un primario deshabilitado sin motivo es el de D-02 y D-06");

        await cut.FindAll(".modal-contenido input").First().InputAsync(new ChangeEventArgs { Value = "Críticos" });

        guardar().HasAttribute("disabled").Should().BeFalse();
        guardar().HasAttribute("title").Should().BeFalse("habilitado no hay motivo que decir");
    }

    [Fact]
    public async Task Un_fallo_al_guardar_el_filtro_se_ve_en_el_aviso_fijo_del_modal_y_lo_escrito_no_se_pierde()
    {
        // Este mediador no conoce GuardarFiltroCommand: lanza, y la pantalla lo recoge como fallo de guardado.
        var cut = Renderizar("guardar-filtro");
        await cut.FindAll(".modal-contenido input").First().InputAsync(new ChangeEventArgs { Value = "Críticos" });

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-aviso .alerta-formulario").TextContent.Should().Contain("No pudimos guardar el filtro");
        cut.FindAll(".modal-cuerpo .alerta-formulario").Should().BeEmpty("el aviso va fuera del cuerpo desplazable (D-20)");
        cut.FindAll(".modal-contenido").Should().NotBeEmpty("el modal sigue abierto: el fallo no cierra ni tira lo escrito");

        await cut.FindAll(".modal-contenido input").First().InputAsync(new ChangeEventArgs { Value = "Críticos 2" });

        cut.FindAll(".modal-aviso").Should().BeEmpty("escribir de nuevo retira el error anterior");
    }

    [Fact]
    public async Task Cancelar_el_filtro_con_el_nombre_escrito_pregunta_como_la_X()
    {
        var cut = Renderizar("guardar-filtro");
        await cut.FindAll(".modal-contenido input").First().InputAsync(new ChangeEventArgs { Value = "Críticos" });

        await cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());

        cut.FindAll("h2").Should().Contain(h => h.TextContent.Trim() == "¿Descartar cambios?");
    }
}
