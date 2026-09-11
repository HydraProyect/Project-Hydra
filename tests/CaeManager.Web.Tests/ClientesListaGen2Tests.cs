using System.Security.Claims;
using System.Text.Json;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Clientes.Commands.CrearCliente;
using CaeManager.Application.Clientes.Commands.EditarCliente;
using CaeManager.Application.Clientes.Commands.EliminarCliente;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Clientes.Commands.RestaurarCliente;
using CaeManager.Application.Clientes.Queries.ObtenerClientePorId;
using CaeManager.Application.Clientes.Queries.ObtenerClientes;
using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion.Commands.EliminarFiltroGuardado;
using CaeManager.Application.Configuracion.Commands.GuardarFiltro;
using CaeManager.Application.Configuracion.Queries;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Tenants;
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
/// Lista de Clientes contra su mockup Gen 2 («Lista Clientes TALVEG.dc.html»).
/// Las filas son Clientes empresariales del Tenant, no Clientes comerciales
/// TALVEG. El vacío por filtro, que ya existía y se conserva, lo sigue
/// probando <see cref="ClientesVacioPorFiltroTests"/>.
///
/// <para>
/// El doble del mediador guarda los clientes y APLICA lo que recibe, igual que
/// <c>ObtenerClientesQueryHandler</c>: filtra por búsqueda (solo razón social),
/// «solo críticos», ejecutivo y estado documental; ordena por
/// <c>OrdenarPor</c>/<c>Descendente</c> con la misma lista blanca; y pagina con
/// <c>Pagina</c>/<c>TamanoPagina</c>. Un doble que ignorase cualquiera de ellos
/// dejaría en verde una pantalla que no lo envía.
/// </para>
///
/// <para>
/// El arnés (autenticación, directorio sin tenant, UserManager que lanza) es el
/// de <see cref="ClientesVacioPorFiltroTests"/>, por las mismas razones que
/// documenta allí: el directorio devuelve vacío sin tocar la base, así que el
/// desplegable de Ejecutivo no tiene opciones.
/// </para>
/// </summary>
public class ClientesListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado y QuickGrid, que importan módulos JS.</summary>
    public ClientesListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public List<ClienteListaDto> Almacen { get; } = [];

        /// <summary>
        /// Estados presentes por Cliente, para el filtro de estado documental:
        /// el handler pregunta si HAY alguno del estado pedido, no si es el peor.
        /// Si un Cliente no aparece aquí, se toma su peor estado como único presente.
        /// </summary>
        public Dictionary<Guid, HashSet<EstadoDocumento>> EstadosPresentes { get; } = [];

        public List<FiltroGuardadoDto> FiltrosGuardados { get; } = [];
        public List<object> Enviadas { get; } = [];

        /// <summary>
        /// Si devuelve una tarea para la petición, esa es la respuesta: permite
        /// retenerla con un <see cref="TaskCompletionSource{TResult}"/> y
        /// resolverla fuera de orden.
        /// </summary>
        public Func<object, Task<object>?>? Retener { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (Retener?.Invoke(request) is { } retenida)
                return (TResponse)await retenida;

            // Síncrono a propósito (mismo motivo que GestionesListaGen2Tests):
            // lo asíncrono de verdad se prueba reteniendo con Retener.
            return (TResponse)Responder(request);
        }

        private object Responder(object request)
        {
            switch (request)
            {
                case ObtenerClientesQuery q:
                    return Filtrar(q);

                case ObtenerFiltrosGuardadosQuery:
                    return (IReadOnlyList<FiltroGuardadoDto>)FiltrosGuardados.ToList();

                case GuardarFiltroCommand g:
                    var nuevo = new FiltroGuardadoDto(Guid.NewGuid(), g.Nombre, g.ValoresJson, DateTime.UtcNow);
                    FiltrosGuardados.Add(nuevo);
                    return Result.Exito(nuevo.Id);

                case EliminarFiltroGuardadoCommand e:
                    FiltrosGuardados.RemoveAll(f => f.Id == e.Id);
                    return Result.Exito();

                case ObtenerClientePorIdQuery p:
                    return Almacen.Where(c => c.Id == p.Id)
                        .Select(c => new ClienteDetalleDto(c.Id, c.RazonSocial, c.Cif, c.EsCritico, null, c.CreadoEnUtc, c.EjecutivoUsuarioId, Guid.NewGuid()))
                        .FirstOrDefault()!;

                case CrearClienteCommand crear:
                    var creado = new ClienteListaDto(Guid.NewGuid(), crear.RazonSocial, crear.Cif, crear.EsCritico, DateTime.UtcNow);
                    Almacen.Add(creado);
                    return Result.Exito(creado.Id);

                case EditarClienteCommand:
                    return Result.Exito();

                case EliminarClienteCommand el:
                    Almacen.RemoveAll(c => c.Id == el.Id);
                    return Result.Exito();

                case EliminarClientesCommand lote:
                    var borrados = Almacen.RemoveAll(c => lote.Ids.Contains(c.Id));
                    return Result.Exito(new ResultadoEliminacionLoteDto(borrados, []));

                case RestaurarClienteCommand:
                    return Result.Exito();

                case ObtenerResumenClienteQuery r:
                    return Almacen.Where(c => c.Id == r.ClienteId)
                        .Select(c => new ResumenClienteDto(c.Id, c.RazonSocial, c.Cif, c.EsCritico, c.CreadoEnUtc, null, c.Centros, 0))
                        .FirstOrDefault()!;

                case ObtenerAgendaContactosQuery:
                    return (IReadOnlyList<ContactoAgendaDto>)Array.Empty<ContactoAgendaDto>();

                default:
                    throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");
            }
        }

        /// <summary>Filtra, ordena y pagina como <c>ObtenerClientesQueryHandler</c>.</summary>
        public ResultadoPaginado<ClienteListaDto> Filtrar(ObtenerClientesQuery q)
        {
            var coincidentes = Almacen
                .Where(c => string.IsNullOrWhiteSpace(q.Busqueda)
                    || c.RazonSocial.ToUpperInvariant().Contains(q.Busqueda.ToUpperInvariant()))
                .Where(c => q.SoloCriticos != true || c.EsCritico)
                .Where(c => q.EjecutivoUsuarioId is null || c.EjecutivoUsuarioId == q.EjecutivoUsuarioId)
                .Where(c => q.EstadoDocumental is null
                    || (q.EstadoDocumental == EstadoDocumento.Vigente
                        ? c.EstadoDocumentalPeor is null
                        : Presentes(c).Contains(q.EstadoDocumental.Value)))
                .ToList();

            var pagina = Ordenar(coincidentes, q.OrdenarPor, q.Descendente)
                .Skip((q.Pagina - 1) * q.TamanoPagina)
                .Take(q.TamanoPagina)
                .ToList();

            return new ResultadoPaginado<ClienteListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
        }

        private HashSet<EstadoDocumento> Presentes(ClienteListaDto c) =>
            EstadosPresentes.TryGetValue(c.Id, out var presentes)
                ? presentes
                : c.EstadoDocumentalPeor is { } peor ? [peor] : [];

        /// <summary>
        /// Misma lista blanca que el handler; cualquier otro nombre (o ninguno)
        /// cae en su orden por defecto, por razón social ascendente. El
        /// desempate es el orden de inserción (OrderBy es estable), no el Id:
        /// con Ids aleatorios, los tests que miran posiciones cambiarían entre
        /// ejecuciones.
        /// </summary>
        private static IEnumerable<ClienteListaDto> Ordenar(List<ClienteListaDto> filas, string? ordenarPor, bool descendente) =>
            (ordenarPor, descendente) switch
            {
                (nameof(ClienteListaDto.RazonSocial), true) => filas.OrderByDescending(c => c.RazonSocial, StringComparer.Ordinal),
                (nameof(ClienteListaDto.Cif), false) => filas.OrderBy(c => c.Cif, StringComparer.Ordinal),
                (nameof(ClienteListaDto.Cif), true) => filas.OrderByDescending(c => c.Cif, StringComparer.Ordinal),
                (nameof(ClienteListaDto.EsCritico), false) => filas.OrderBy(c => c.EsCritico).ThenBy(c => c.RazonSocial, StringComparer.Ordinal),
                (nameof(ClienteListaDto.EsCritico), true) => filas.OrderByDescending(c => c.EsCritico).ThenBy(c => c.RazonSocial, StringComparer.Ordinal),
                (nameof(ClienteListaDto.CreadoEnUtc), false) => filas.OrderBy(c => c.CreadoEnUtc),
                (nameof(ClienteListaDto.CreadoEnUtc), true) => filas.OrderByDescending(c => c.CreadoEnUtc),
                _ => filas.OrderBy(c => c.RazonSocial, StringComparer.Ordinal)
            };

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

    // --- Arnés: el mismo de ClientesVacioPorFiltroTests (ver sus comentarios) ---

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

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

    /// <summary>
    /// El rol decide un camino de la página: solo Administrador, Dirección CAE
    /// y Coordinación CAE pueden reasignar el Gestor CAE, y para ellos «Editar»
    /// hace una segunda espera (el directorio de gestores). Por eso el rol es
    /// configurable: un caso que solo corriera como Administrador no vería el
    /// camino de un Gestor CAE.
    /// </summary>
    private sealed class AutenticacionFalsa(string rol) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "test"))));
    }

    private static DirectorioUsuariosTenant CrearDirectorio()
    {
        var tenantActual = new TenantActualFalso();
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(ClientesListaGen2Tests)),
            tenantActual);

        var userManager = new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!);

        return new DirectorioUsuariosTenant(
            userManager, new TenantsQueryContextQueNadieDebeTocar(), tenantActual,
            new PuertaAccesoDatos(), identidad);
    }

    private void Registrar(MediatorFalso mediador, string rol = Roles.Administrador)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped(_ => CrearDirectorio());
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddScoped<PuertaAccesoDatos>();
    }

    /// <param name="url">Ruta relativa con la que se abre la página (p. ej. <c>clientes?critico=true</c>).</param>
    private IRenderedComponent<Clientes> Renderizar(MediatorFalso mediador, string url = "clientes", string rol = Roles.Administrador)
    {
        Registrar(mediador, rol);
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<Clientes>();
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("aria-busy=\"true\""));
        return cut;
    }

    private static ClienteListaDto Cliente(
        string razonSocial, string cif = "A-48.010.615", bool critico = false, int centros = 0,
        EstadoDocumento? peor = null, int cantidad = 0) =>
        new(Guid.NewGuid(), razonSocial, cif, critico, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Centros: centros, EstadoDocumentalPeor: peor, EstadoDocumentalCantidad: cantidad);

    private static List<string> NombresDeLasFilas(IRenderedComponent<Clientes> cut) =>
        cut.FindAll("tbody tr td .enlace-nombre-fila").Select(b => b.TextContent.Trim()).ToList();

    private static ObtenerClientesQuery UltimaConsulta(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerClientesQuery>().Last();

    private static IElement SelectConOpcion(IRenderedComponent<Clientes> cut, string textoOpcion) =>
        cut.FindAll(".barra-filtros select").Single(s => s.TextContent.Contains(textoOpcion));

    private static IElement BotonDelDialogo(IRenderedComponent<Clientes> cut, string texto) =>
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == texto);

    /// <summary>
    /// El ítem se busca DENTRO del menú de esa fila (MenuAcciones pinta su panel
    /// en línea): con una acción de otra fila aún en vuelo, su menú sigue abierto
    /// —se cierra al terminar el manejador— y una búsqueda global encontraría
    /// dos «Editar».
    /// </summary>
    private static async Task PulsarEnElMenuDeLaFila(IRenderedComponent<Clientes> cut, int fila, string item)
    {
        await cut.FindAll("tbody .menu-acciones")[fila].QuerySelector(".menu-acciones-disparador")!.ClickAsync(new MouseEventArgs());
        await cut.FindAll("tbody .menu-acciones")[fila].QuerySelectorAll(".menu-acciones-item")
            .Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    // ------------------------------------------------------------------ Cabecera

    [Fact]
    public void La_cabecera_es_la_Gen_2_con_su_kicker_y_conserva_las_acciones_que_ya_tenia()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } });

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker")!.TextContent.Trim().Should().Be("Negocio");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Clientes");

        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.TextContent.Should().Contain("Exportar a Excel").And.Contain("Alta guiada").And.Contain("+ Nuevo cliente");
        acciones.QuerySelectorAll("a").Select(a => a.GetAttribute("href"))
            .Should().Contain(["/clientes/exportar.xlsx", "/clientes/alta-guiada", "/importacion?plantilla=clientes"],
                "las importaciones (Administrador) y el alta guiada ya estaban y el mockup no las retira");
    }

    [Fact]
    public async Task Nuevo_cliente_de_la_cabecera_abre_el_drawer_de_alta()
    {
        var cut = Renderizar(new MediatorFalso());
        cut.FindAll(".drawer-panel").Should().BeEmpty("punto de partida: el drawer está cerrado");

        await cut.Find("header.cabecera-pagina .acciones-cabecera button").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo cliente");
    }

    [Fact]
    public void El_buscador_no_promete_buscar_por_alias_ni_CIF()
    {
        var cut = Renderizar(new MediatorFalso());

        cut.Find(".barra-filtros input[type=text]").GetAttribute("placeholder").Should().Be("Buscar por nombre…",
            "ObtenerClientesQuery solo busca en la razón social y el DTO no tiene alias; el E2E P331 usa este texto");
        cut.Markup.Should().NotContain("alias o CIF");
    }

    // --------------------------------------------------------- Conteo y filtros

    [Fact]
    public void Sin_filtros_el_conteo_dice_cuantos_hay_sin_hablar_de_filtros()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L."), Cliente("Grúas Aldapa S.L.") } });

        cut.Find(".conteo-clientes").TextContent.Trim().Should().Be("3 clientes");
    }

    /// <summary>
    /// El doble filtra por <c>SoloCriticos</c>: de tres, uno es crítico. Si la
    /// pantalla no enviara el <c>?critico=</c> de la URL, el conteo diría 3.
    /// </summary>
    [Fact]
    public void Con_solo_criticos_en_la_url_la_consulta_lo_lleva_y_el_conteo_lo_dice()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", critico: true), Cliente("Montajes Ebro S.L."), Cliente("Grúas Aldapa S.L.") }
        };
        var cut = Renderizar(mediador, "clientes?critico=true");

        UltimaConsulta(mediador).SoloCriticos.Should().BeTrue();
        cut.Find(".conteo-clientes").TextContent.Trim().Should().Be("1 cliente con estos filtros");
        NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]);
    }

    [Fact]
    public async Task Escribir_en_el_buscador_manda_la_busqueda_la_escribe_en_la_url_y_recorta_las_filas()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L.") } };
        var cut = Renderizar(mediador);

        // InputAsync espera al debounce de CampoTexto (300 ms) y a su recarga.
        await cut.Find(".barra-filtros input[type=text]").InputAsync(new ChangeEventArgs { Value = "refri" });

        UltimaConsulta(mediador).Busqueda.Should().Be("refri");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=refri");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
    }

    /// <summary>
    /// «Con vencidos» pregunta si HAY algún vencido, no si el peor es vencido:
    /// Montajes tiene un faltante (peor) y además vencidos, y tiene que salir.
    /// </summary>
    [Fact]
    public async Task El_filtro_de_estado_documental_viaja_en_la_consulta_y_se_ve_como_chip()
    {
        var montajes = Cliente("Montajes Ebro S.L.", peor: EstadoDocumento.Faltante, cantidad: 1);
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 12), montajes, Cliente("Grúas Aldapa S.L.") }
        };
        mediador.EstadosPresentes[montajes.Id] = [EstadoDocumento.Faltante, EstadoDocumento.Vencido];
        var cut = Renderizar(mediador);

        await SelectConOpcion(cut, "Estado: todos").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });

        UltimaConsulta(mediador).EstadoDocumental.Should().Be(EstadoDocumento.Vencido);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Montajes Ebro S.L.", "Refrielectric S.A."]));
        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().ContainSingle(t => t.StartsWith("Estado: Vencido"));
    }

    [Fact]
    public async Task El_filtro_de_ejecutivo_viaja_en_la_consulta()
    {
        var gestora = Guid.NewGuid();
        var deLaGestora = Cliente("Refrielectric S.A.") with { EjecutivoUsuarioId = gestora };
        var mediador = new MediatorFalso { Almacen = { deLaGestora, Cliente("Montajes Ebro S.L.") } };
        var cut = Renderizar(mediador);

        await SelectConOpcion(cut, "Ejecutivo: todos").ChangeAsync(new ChangeEventArgs { Value = gestora.ToString() });

        UltimaConsulta(mediador).EjecutivoUsuarioId.Should().Be(gestora);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
    }

    /// <summary>
    /// «Limpiar todo» (mockup) y «Quitar los filtros» (estado vacío) son el
    /// mismo LimpiarFiltrosAsync: quitan los cuatro filtros y los dos de la URL.
    /// Si dejara <c>critico</c> en la URL, la siguiente pasada de parámetros lo
    /// devolvería.
    /// </summary>
    [Fact]
    public async Task Limpiar_todo_quita_los_filtros_de_la_consulta_y_de_la_url()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", critico: true), Cliente("Montajes Ebro S.L.") }
        };
        var cut = Renderizar(mediador, "clientes?q=Refri&critico=true");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        navegacion.Uri.Should().Contain("critico=true", "es el punto de partida de este caso");
        cut.FindAll(".chip-filtro").Should().HaveCount(2);

        await cut.Find("button.limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("critico").And.NotContain("q=");
        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().BeNull();
        consulta.SoloCriticos.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().BeEmpty());
        cut.WaitForAssertion(() => cut.Find(".conteo-clientes").TextContent.Trim().Should().Be("2 clientes"));
    }

    // ------------------------------------------------------- Página y orden

    /// <summary>
    /// 25 clientes: la página 1 son los 20 primeros por razón social y la 2, los
    /// cinco últimos. El doble pagina con lo que recibe; si la pantalla mandara
    /// siempre la página 1, la segunda repetiría la primera.
    /// </summary>
    [Fact]
    public async Task Pasar_a_la_pagina_siguiente_pide_la_pagina_2_y_pinta_sus_filas()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}"));
        var cut = Renderizar(mediador);
        NombresDeLasFilas(cut).Should().HaveCount(20).And.StartWith("Cliente 01");

        await cut.FindAll(".paginador-simple button").Single(b => b.TextContent.Contains("Siguiente")).ClickAsync(new MouseEventArgs());

        var consulta = UltimaConsulta(mediador);
        consulta.Pagina.Should().Be(2);
        consulta.TamanoPagina.Should().Be(20);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(
            ["Cliente 21", "Cliente 22", "Cliente 23", "Cliente 24", "Cliente 25"]));
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 cliente(s)");
    }

    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_lo_manda_y_vuelve_a_la_primera()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 60; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}"));
        var cut = Renderizar(mediador);

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        var consulta = UltimaConsulta(mediador);
        consulta.TamanoPagina.Should().Be(50);
        consulta.Pagina.Should().Be(1);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(50));
    }

    private static IElement CabeceraOrdenable(IRenderedComponent<Clientes> cut, string titulo) =>
        cut.FindAll("thead th").Single(th => th.TextContent.Trim() == titulo).QuerySelector("button")!;

    /// <summary>
    /// El orden por defecto (razón social) no coincide con el de CIF ni con su
    /// inverso: si la pantalla no enviara el orden de la cabecera, las filas no
    /// cambiarían.
    /// </summary>
    [Fact]
    public async Task Pulsar_la_cabecera_CIF_ordena_la_consulta_por_CIF_y_la_segunda_vez_al_reves()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Aislamientos Nervión S.L.", cif: "B-95.410.882"),
                Cliente("Montajes Ebro S.L.", cif: "B-50.331.406"),
                Cliente("Refrielectric S.A.", cif: "A-48.220.917"),
            }
        };
        var cut = Renderizar(mediador);
        NombresDeLasFilas(cut).Should().Equal(["Aislamientos Nervión S.L.", "Montajes Ebro S.L.", "Refrielectric S.A."]);

        await CabeceraOrdenable(cut, "CIF").ClickAsync(new MouseEventArgs());

        var ascendente = UltimaConsulta(mediador);
        ascendente.OrdenarPor.Should().Be(nameof(ClienteListaDto.Cif));
        ascendente.Descendente.Should().BeFalse();
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(
            ["Refrielectric S.A.", "Montajes Ebro S.L.", "Aislamientos Nervión S.L."]));

        await CabeceraOrdenable(cut, "CIF").ClickAsync(new MouseEventArgs());

        var descendente = UltimaConsulta(mediador);
        descendente.OrdenarPor.Should().Be(nameof(ClienteListaDto.Cif));
        descendente.Descendente.Should().BeTrue("la segunda pulsación invierte el orden");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(
            ["Aislamientos Nervión S.L.", "Montajes Ebro S.L.", "Refrielectric S.A."]));
    }

    // ------------------------------------------------------------ Carreras

    /// <summary>
    /// La primera carga (sin filtros) tarda; mientras tanto se marca «Solo
    /// críticos», que responde en seguida sin nada. Cuando la vieja llega con
    /// tres clientes, no puede pisar el total: la pantalla sigue diciendo que
    /// ninguno coincide y no pinta ningún conteo.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_lenta_de_la_carga_anterior_no_pisa_el_resultado_del_filtro_nuevo()
    {
        var respuestaVieja = new TaskCompletionSource<object>();
        var retenida = false;
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L."), Cliente("Grúas Aldapa S.L.") }
        };
        mediador.Retener = p =>
        {
            if (retenida || p is not ObtenerClientesQuery { SoloCriticos: null }) return null;
            retenida = true;
            return respuestaVieja.Task;
        };
        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes");
        var cut = Render<Clientes>();

        await cut.Find(".filtro-critico input").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ningún cliente con estos filtros"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            mediador.Filtrar(new ObtenerClientesQuery(null, null))));

        // Cualquier repintado posterior enseña el estado que dejó la respuesta
        // vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ningún cliente con estos filtros",
            "la respuesta vieja era de la lista sin filtrar, no de la pregunta vigente");
        cut.FindAll(".conteo-clientes").Should().BeEmpty("no hay coincidencias con el filtro vigente");
    }

    /// <summary>
    /// «Editar» sobre A tarda; mientras tanto se pide «Editar» sobre B, que
    /// responde en seguida. Cuando A llega, el formulario sigue siendo el de B.
    ///
    /// <para>
    /// <b>Por qué dos roles.</b> Una mutación que quitaba la guarda tras la
    /// consulta del Cliente SOBREVIVIÓ con el caso solo como Administrador: para
    /// quien puede reasignar, «Editar» hace además la consulta del directorio y
    /// hay una segunda guarda detrás, que era la que atrapaba la respuesta
    /// tardía. Un Gestor CAE no pasa por ahí, y para él la primera guarda es la
    /// única: sin este caso, nadie la observaba.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.GestorCae)]
    public async Task Una_consulta_de_edicion_lenta_no_rellena_el_formulario_de_otro_cliente(string rol)
    {
        var a = Cliente("Refrielectric S.A.");
        var b = Cliente("Montajes Ebro S.L.");
        var respuestaDeA = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso
        {
            Almacen = { a, b },
            Retener = p => p is ObtenerClientePorIdQuery q && q.Id == a.Id ? respuestaDeA.Task : null
        };
        var cut = Renderizar(mediador, rol: rol);
        var filaA = NombresDeLasFilas(cut).IndexOf(a.RazonSocial);
        var filaB = NombresDeLasFilas(cut).IndexOf(b.RazonSocial);

        // Sin await del de A: su manejador espera a la consulta retenida.
        var edicionDeA = PulsarEnElMenuDeLaFila(cut, filaA, "Editar");
        await PulsarEnElMenuDeLaFila(cut, filaB, "Editar");
        cut.WaitForAssertion(() => cut.Find(".drawer-panel input").GetAttribute("value").Should().Be(b.RazonSocial));

        await cut.InvokeAsync(() => respuestaDeA.SetResult(
            new ClienteDetalleDto(a.Id, a.RazonSocial, a.Cif, false, null, a.CreadoEnUtc, null, Guid.NewGuid())));
        await edicionDeA;
        cut.Render();

        cut.Find(".drawer-panel input").GetAttribute("value").Should().Be(b.RazonSocial,
            "la respuesta de A llegó tarde: el formulario abierto es el de B");
    }

    /// <summary>
    /// Mientras viaja el alta, un segundo «Guardar» no manda otra: crearía el
    /// mismo Cliente empresarial dos veces.
    /// </summary>
    [Fact]
    public async Task Un_segundo_Guardar_mientras_viaja_el_alta_no_manda_otra()
    {
        var respuesta = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Retener = p => p is CrearClienteCommand ? respuesta.Task : null };
        var cut = Renderizar(mediador);

        await cut.Find("header.cabecera-pagina .acciones-cabecera button").ClickAsync(new MouseEventArgs());
        var primero = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearClienteCommand>().Should().HaveCount(1, "el segundo clic llega con el alta en vuelo");

        await cut.InvokeAsync(() => respuesta.SetResult(Result.Exito(Guid.NewGuid())));
        await Task.WhenAll(primero, segundo);
    }

    // ------------------------------------------------------ Acción por URL

    /// <summary>
    /// El atajo global «n» navega a <c>/clientes?accion=crear</c> ESTANDO ya en
    /// /clientes: el componente no se recrea. Antes la acción solo se leía al
    /// montar y «n» cambiaba la URL sin abrir nada. Y al cerrar se quita de la
    /// URL, para que un segundo «n» vuelva a abrir el alta.
    /// </summary>
    [Fact]
    public async Task Pedir_crear_por_url_estando_ya_en_la_lista_abre_el_alta_y_se_puede_repetir()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } });
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.FindAll(".drawer-panel").Should().BeEmpty("punto de partida");

        await cut.InvokeAsync(() => navegacion.NavigateTo("clientes?accion=crear"));
        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo cliente"));

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();
        navegacion.Uri.Should().NotContain("accion=", "si se quedara, el siguiente «n» navegaría a la misma URL y no abriría nada");

        await cut.InvokeAsync(() => navegacion.NavigateTo("clientes?accion=crear"));
        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo cliente"));
    }

    /// <summary>
    /// Mientras la URL conserva <c>accion=guardar-filtro</c>, escribir otro filtro
    /// en la URL no puede volver a abrir el modal que ya se cerró.
    /// </summary>
    [Fact]
    public async Task Cerrar_el_modal_de_guardar_filtro_pedido_por_url_no_lo_reabre_al_filtrar()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.", critico: true) } }, "clientes?accion=guardar-filtro");
        cut.WaitForAssertion(() => cut.Find("[role=dialog] h2").TextContent.Trim().Should().Be("Guardar filtro actual"));

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());
        await cut.Find(".filtro-critico input").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.FindAll("[role=dialog]").Should().BeEmpty("el modal ya se atendió y se cerró");
    }

    // ------------------------------------------------------------- Filas

    [Fact]
    public void El_estado_documental_concuerda_el_recuento_y_explica_de_donde_sale()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen =
            {
                Cliente("Aislamientos Nervión S.L.", peor: EstadoDocumento.Vencido, cantidad: 1),
                Cliente("Montajes Ebro S.L.", peor: EstadoDocumento.Proximo, cantidad: 9),
                Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 12),
                Cliente("Talleres Berriz Coop."),
            }
        });

        var badges = cut.FindAll("tbody .badge");
        badges.Select(b => b.TextContent.Trim()).Should().Equal(["1 vencido", "9 próximos", "12 vencidos", "Al corriente"]);
        badges[2].GetAttribute("title").Should().Contain("trabajadores").And.NotContain("centros",
            "el agregado de ObtenerClientesQuery sale de las alertas de sus trabajadores; los centros no entran");
    }

    [Fact]
    public async Task El_numero_de_centros_abre_el_Cliente_360_en_su_pestana_de_centros()
    {
        var cliente = Cliente("Refrielectric S.A.", centros: 3);
        var cut = Renderizar(new MediatorFalso { Almacen = { cliente, Cliente("Montajes Ebro S.L.") } });

        cut.FindAll("tbody a[href^='/centros']").Should().BeEmpty(
            "/centros no filtra por clienteId: el enlace llevaba a todos los centros");

        await cut.Find(".enlace-centros-cliente").ClickAsync(new MouseEventArgs());

        var frame = Services.GetRequiredService<ContextWorkspaceService>().FrameActual;
        frame.Should().NotBeNull();
        frame!.Tipo.Should().Be(EntidadWorkspace.Cliente);
        frame.EntidadId.Should().Be(cliente.Id);
        frame.PestanaActiva.Should().Be("centros");
    }

    [Fact]
    public async Task Abrir_Cliente_360_del_menu_abre_el_workspace_de_esa_fila()
    {
        var otro = Cliente("Aislamientos Nervión S.L.");
        var abierto = Cliente("Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Almacen = { otro, abierto } });

        await PulsarEnElMenuDeLaFila(cut, 1, "Abrir Cliente 360");

        var frame = Services.GetRequiredService<ContextWorkspaceService>().FrameActual;
        frame.Should().NotBeNull();
        frame!.EntidadId.Should().Be(abierto.Id);
        frame.PestanaActiva.Should().Be("informacion");
    }

    [Fact]
    public async Task Vista_rapida_del_menu_abre_la_vista_rapida_de_esa_fila_y_no_el_360()
    {
        var otro = Cliente("Aislamientos Nervión S.L.");
        var abierto = Cliente("Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Almacen = { otro, abierto } });

        await PulsarEnElMenuDeLaFila(cut, 1, "Vista rápida");

        cut.WaitForAssertion(() => cut.Find("aside.drawer-preview-cliente .nombre-cabecera-preview-cliente")
            .TextContent.Trim().Should().Be("Montajes Ebro S.L."));
        cut.Find("aside.drawer-preview-cliente .etiqueta-cabecera-preview-cliente").TextContent.Trim()
            .Should().Be("Vista rápida · Cliente");
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse();
    }

    // ------------------------------------------------------ Teclado y lote

    /// <summary>
    /// Lo mismo que recorre el E2E P331 (j/k/x/Enter sin activar «Selección
    /// múltiple»), aquí sin navegador: j enfoca, x marca y la barra de lote
    /// dice «1 seleccionado en esta página», Enter abre la vista rápida.
    /// </summary>
    [Fact]
    public async Task Los_atajos_j_x_y_Enter_enfocan_marcan_y_abren_la_vista_rapida()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Aislamientos Nervión S.L."), Cliente("Montajes Ebro S.L.") } });
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        cut.FindAll("tbody tr")[1].ClassList.Should().Contain("fila-enfocada");

        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("x"));
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");

        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("Enter"));
        cut.WaitForAssertion(() => cut.Find("aside.drawer-preview-cliente .nombre-cabecera-preview-cliente")
            .TextContent.Trim().Should().Be("Montajes Ebro S.L."));
    }

    /// <summary>
    /// Con los 20 de la página marcados y 25 coincidencias, el aviso dice que
    /// los otros cinco no entran, y no ofrece «Seleccionar los 25 filtrados»:
    /// sería selección masiva sobre un camino que borra.
    /// </summary>
    [Fact]
    public async Task Marcar_toda_la_pagina_avisa_de_que_las_otras_paginas_no_entran()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}"));
        var cut = Renderizar(mediador);

        await cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());
        cut.FindAll(".aviso-seleccion-pagina").Should().BeEmpty("aún no hay nada marcado");

        await cut.Find("thead input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.Find(".aviso-seleccion-pagina").TextContent.Trim().Should().Be(
            "Los 20 de esta página están seleccionados. Hay 25 en total: los de otras páginas no entran en la selección.");
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("20 seleccionados en esta página");
        cut.Markup.Should().NotContain("filtrados");
    }

    [Fact]
    public async Task Eliminar_en_lote_pide_confirmacion_y_manda_solo_los_marcados()
    {
        var a = Cliente("Aislamientos Nervión S.L.");
        var b = Cliente("Montajes Ebro S.L.");
        var c = Cliente("Refrielectric S.A.");
        var mediador = new MediatorFalso { Almacen = { a, b, c } };
        var cut = Renderizar(mediador);

        await cut.FindAll(".barra-herramientas-lista button").Single(x => x.TextContent.Trim() == "Selección múltiple").ClickAsync(new MouseEventArgs());
        await cut.FindAll("tbody input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("tbody input[type=checkbox]")[2].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(x => x.TextContent.Trim() == "Eliminar seleccionados").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClientesCommand>().Should().BeEmpty("abrir el diálogo no borra nada");
        cut.Find("[role=dialog] h2").TextContent.Should().Be("¿Eliminar 2 cliente(s)?");

        await BotonDelDialogo(cut, "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClientesCommand>().Single().Ids.Should().BeEquivalentTo([a.Id, c.Id]);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Montajes Ebro S.L."]));
    }

    // ---------------------------------------------------- Filtros guardados

    [Fact]
    public async Task Guardar_filtro_guarda_los_cuatro_filtros_y_no_solo_busqueda_y_criticos()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.", critico: true, peor: EstadoDocumento.Vencido, cantidad: 2) } };
        var cut = Renderizar(mediador, "clientes?critico=true");
        await SelectConOpcion(cut, "Estado: todos").ChangeAsync(new ChangeEventArgs { Value = nameof(EstadoDocumento.Vencido) });

        await cut.FindAll(".barra-filtros button").Single(b => b.TextContent.Trim() == "Guardar filtro").ClickAsync(new MouseEventArgs());
        await cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = "Críticos con vencidos" });
        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var guardado = mediador.Enviadas.OfType<GuardarFiltroCommand>().Single();
        guardado.Nombre.Should().Be("Críticos con vencidos");
        using var json = JsonDocument.Parse(guardado.ValoresJson);
        json.RootElement.GetProperty("SoloCriticos").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("EstadoDocumental").GetString().Should().Be(nameof(EstadoDocumento.Vencido));
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog]").Should().BeEmpty());
    }

    /// <summary>
    /// Aplicar un filtro guardado repone los cuatro filtros y escribe en la URL
    /// los dos que viajan por ella. Si solo los cambiara en memoria, la URL
    /// seguiría sin <c>q</c> ni <c>critico</c> y la siguiente pasada de
    /// parámetros los quitaría.
    /// </summary>
    [Fact]
    public async Task Aplicar_un_filtro_guardado_repone_sus_filtros_en_la_consulta_y_en_la_url()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Refri críticos con vencidos",
            JsonSerializer.Serialize(new { Busqueda = "Refri", SoloCriticos = true, GestorCaeId = (string?)null, EstadoDocumental = "Vencido" }),
            DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Refrielectric S.A.", critico: true, peor: EstadoDocumento.Vencido, cantidad: 3),
                Cliente("Refrigeración Norte S.L.", critico: true),
                Cliente("Montajes Ebro S.L."),
            },
            FiltrosGuardados = { filtro }
        };
        var cut = Renderizar(mediador);

        await SelectConOpcion(cut, "Filtros guardados…").ChangeAsync(new ChangeEventArgs { Value = filtro.Id.ToString() });

        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().Be("Refri");
        consulta.SoloCriticos.Should().BeTrue();
        consulta.EstadoDocumental.Should().Be(EstadoDocumento.Vencido);
        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain("q=Refri").And.Contain("critico=true");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
    }

    /// <summary>Un filtro guardado antes de este cambio solo trae búsqueda y críticos: se sigue aplicando.</summary>
    [Fact]
    public async Task Un_filtro_guardado_con_el_formato_antiguo_se_sigue_aplicando()
    {
        var antiguo = new FiltroGuardadoDto(Guid.NewGuid(), "Solo Refri", "{\"Busqueda\":\"Refri\",\"SoloCriticos\":false}", DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L.") },
            FiltrosGuardados = { antiguo }
        };
        var cut = Renderizar(mediador);

        await SelectConOpcion(cut, "Filtros guardados…").ChangeAsync(new ChangeEventArgs { Value = antiguo.Id.ToString() });

        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().Be("Refri");
        consulta.EstadoDocumental.Should().BeNull();
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
    }

    [Fact]
    public async Task Borrar_un_filtro_guardado_pide_confirmacion_con_su_efecto_y_solo_entonces_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Cartera Levante", "{\"Busqueda\":null,\"SoloCriticos\":true}", DateTime.UtcNow);
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") }, FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await cut.FindAll(".chip-filtro").Single(c => c.TextContent.Contains("Cartera Levante"))
            .QuerySelector(".chip-filtro-quitar")!.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty("pulsar el aspa solo pide confirmación");
        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector("h2")!.TextContent.Should().Be("¿Borrar el filtro guardado «Cartera Levante»?");
        dialogo.TextContent.Should().Contain("No borra ningún cliente");

        await BotonDelDialogo(cut, "Borrar filtro").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().Equal([new EliminarFiltroGuardadoCommand(filtro.Id)]);
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().NotContain(c => c.TextContent.Contains("Cartera Levante")));
        cut.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelar_el_borrado_de_un_filtro_guardado_no_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Cartera Levante", "{\"Busqueda\":null,\"SoloCriticos\":true}", DateTime.UtcNow);
        var mediador = new MediatorFalso { FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await cut.Find(".chip-filtro .chip-filtro-quitar").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty();
        cut.FindAll(".chip-filtro").Should().ContainSingle(c => c.TextContent.Contains("Cartera Levante"));
    }
}
