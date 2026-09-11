using System.Security.Claims;
using Bunit;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Tenants;
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
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Cierra por render el hueco declarado del trinquete para Clientes, la lista
/// con <b>más filtros de todo el producto</b>: búsqueda, «solo críticos»,
/// ejecutivo y estado documental. Aun así decía «Aún no hay clientes» con los
/// cuatro puestos.
///
/// <para>
/// <b>Su arnés es el más caro de las cuatro</b>, y por eso llevaba sin él: la
/// página inyecta <c>AuthenticationStateProvider</c> —para decidir si el rol
/// puede reasignar el Gestor CAE dueño— y <c>DirectorioUsuariosTenant</c>, que
/// es una clase concreta con cinco dependencias, no una interfaz. Se instancia
/// de verdad, con un <see cref="TenantActualFalso"/> sin tenant: ese es el
/// camino documentado de <c>ObtenerVisiblesEnRolAsync</c> que devuelve vacío
/// <b>sin tocar la base</b> — «sin tenant resuelto devuelve vacío, no todo».
/// El selector de ejecutivo queda sin opciones, que no es lo que estos casos
/// miran.
/// </para>
/// </summary>
public class ClientesVacioPorFiltroTests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public ClientesVacioPorFiltroTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<ClienteListaDto> Clientes { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerFiltrosGuardadosQuery => (object)Array.Empty<FiltroGuardadoDto>(),
                ObtenerClientesQuery q => new ResultadoPaginado<ClienteListaDto>(
                    Clientes, Clientes.Count, q.Pagina, q.TamanoPagina),
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
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>
    /// Sin tenant a propósito: es lo que hace que el directorio devuelva vacío
    /// sin consultar nada. Ver el comentario de la clase.
    /// </summary>
    private sealed class TenantActualFalso : ITenantActual
    {
        public Guid? TenantId => null;
    }

    /// <summary>Nunca se llega a él con el tenant sin resolver; lanza para enterarnos si eso cambia.</summary>
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
    /// Rol Administrador: el MÁS permisivo. Si el estado vacío dependiera del
    /// rol, este es el que más pintaría, así que es el que menos ayuda a que
    /// estos casos pasen por casualidad.
    /// </summary>
    /// <summary>
    /// bUnit registra un <c>IAuthorizationService</c> que lanza si no se usa su
    /// propio helper, y gana sobre <c>AddAuthorizationCore</c>. Este lo
    /// sustituye evaluando los roles <b>de verdad</b> contra el principal: uno
    /// que autorizara siempre sería más permisivo que Administrador y dejaría
    /// pasar cualquier cosa que la página oculte por rol.
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

    private static ClienteListaDto Cliente(string razonSocial) =>
        new(Guid.NewGuid(), razonSocial, "A-48.010.615", EsCritico: false, DateTime.UtcNow);

    /// <param name="busqueda">Filtro de texto que llega por la URL (?q=).</param>
    /// <param name="soloCriticos">Filtro de críticos que llega por la URL (?critico=).</param>
    private IRenderedComponent<Clientes> Renderizar(string? busqueda = null, bool soloCriticos = false,
        params ClienteListaDto[] clientes)
    {
        Services.AddScoped<IMediator>(_ => new MediatorPorTipo { Clientes = clientes });
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        // La página lleva AuthorizeView y pregunta por el rol para decidir si
        // puede reasignar el Gestor CAE dueño de un Cliente. El doble de bUnit
        // registra de una vez el AuthenticationStateProvider, el
        // IAuthorizationService y el policy provider — montarlo a mano dejaba
        // los siete casos cayendo por el arnés, no por lo que dicen observar.
        //
        // Administrador es el rol MÁS permisivo: si el estado vacío dependiera
        // del rol, este es el que más pintaría, así que es el que menos ayuda a
        // que estos casos pasen por casualidad.
        Services.AddScoped<AuthenticationStateProvider, AutenticacionFalsa>();
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped(_ => CrearDirectorio());

        // ClientePreviewDrawer, que la página monta siempre aunque esté cerrado,
        // inyecta UserManager por su cuenta. Con la lista vacía no consulta a
        // nadie: el almacén lanza si alguien lo toca.
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddScoped<PuertaAccesoDatos>();

        var partes = new List<string>();
        if (!string.IsNullOrWhiteSpace(busqueda)) partes.Add("q=" + Uri.EscapeDataString(busqueda));
        if (soloCriticos) partes.Add("critico=true");
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(partes.Count == 0 ? "clientes" : "clientes?" + string.Join('&', partes));

        return Render<Clientes>();
    }

    /// <summary>
    /// <c>DirectorioUsuariosTenant</c> es una clase concreta, no una interfaz,
    /// así que se construye de verdad con dobles en sus cinco dependencias. El
    /// <c>DbContextOptions</c> va sin proveedor a propósito: construir un
    /// DbContext no abre conexión, y en el camino sin tenant no se consulta —
    /// si alguien lo consultara, el fallo sería ruidoso en vez de silencioso.
    /// </summary>
    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(ClientesVacioPorFiltroTests)),
            tenantActual);

        var userManager = new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!);

        return new DirectorioUsuariosTenant(
            userManager, new TenantsQueryContextQueNadieDebeTocar(), tenantActual,
            new PuertaAccesoDatos(), identidad);
    }

    [Fact]
    public void Sin_resultados_y_con_busqueda_no_invita_a_crear_el_primero()
    {
        var cut = Renderizar(busqueda: "Refrielectric");

        cut.Markup.Should().Contain("Ningún cliente con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay clientes",
            "mandar a crear a quien acaba de buscar termina en un cliente duplicado");
    }

    [Fact]
    public void Sin_resultados_y_con_solo_criticos_tambien_lo_distingue()
    {
        var cut = Renderizar(soloCriticos: true);

        cut.Markup.Should().Contain("Ningún cliente con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay clientes",
            "«solo críticos» sin resultados es una buena noticia, no una lista vacía");
    }

    [Fact]
    public void Sin_filtros_y_sin_registros_sigue_invitando_a_crear_el_primero()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay clientes");
        cut.Markup.Should().Contain("Crea el primero para empezar a organizar tus centros de trabajo.");
        cut.Markup.Should().NotContain("Ningún cliente con estos filtros");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado.
    /// La barrera va delante — una aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_que_existan_clientes_dados_de_alta()
    {
        var cut = Renderizar(busqueda: "Refrielectric");

        cut.Markup.Should().Contain("Ningún cliente con estos filtros");
        cut.Markup.Should().NotContain("Hay clientes dados de alta");
    }

    /// <summary>
    /// <b>Este caso encontró un defecto real</b>, y por eso mira la URL y no
    /// solo lo pintado: «Quitar los filtros» limpiaba <c>q</c> de la URL pero
    /// dejaba <c>critico=true</c>, y <c>OnParametersSetAsync</c> —que
    /// re-sincroniza desde la URL— lo devolvía a true en la siguiente pasada.
    /// Pulsar el botón con «solo críticos» activo dejaba la lista igual de
    /// recortada y el chip reaparecía. El trinquete de fuente lo daba por
    /// bueno: solo comprueba que exista la rama, no que el botón funcione.
    /// </summary>
    [Fact]
    public void Quitar_los_filtros_borra_los_dos_de_la_url_y_no_solo_la_busqueda()
    {
        var cut = Renderizar(busqueda: "Refrielectric", soloCriticos: true);
        cut.Markup.Should().Contain("Ningún cliente con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().NotContain("critico", "dejarlo en la URL lo devuelve en la siguiente pasada de parámetros");
        uri.Should().NotContain("q=Refrielectric");

        cut.Markup.Should().NotContain("Ningún cliente con estos filtros");
        cut.Markup.Should().Contain("Aún no hay clientes");
        cut.FindAll(".chip-filtro").Should().BeEmpty("el chip de «solo críticos» volvía a aparecer");
    }

    /// <summary>
    /// Con cuatro filtros posibles, los chips son lo único que deja ver desde
    /// fuera qué está recortando la lista. Dos puestos, dos chips.
    /// </summary>
    [Fact]
    public void Los_filtros_activos_se_ven_como_chips()
    {
        var cut = Renderizar(busqueda: "Refrielectric", soloCriticos: true,
            clientes: Cliente("Refrielectric S.A."));

        cut.FindAll(".chip-filtro").Should().HaveCount(2, "hay dos filtros puestos: la búsqueda y «solo críticos»");
        cut.Markup.Should().Contain("Refrielectric");
    }

    [Fact]
    public void Con_resultados_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(busqueda: "Refrielectric", clientes: Cliente("Refrielectric S.A."));

        cut.Markup.Should().NotContain("Ningún cliente con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay clientes");
        cut.Markup.Should().Contain("Refrielectric S.A.");
    }
}
