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
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
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
/// desplegable de Ejecutivo no tiene opciones. Los casos que necesitan Gestores
/// CAE que elegir los pasan a <see cref="Renderizar"/>: entonces el directorio
/// tiene un tenant resuelto, un almacén que devuelve esos Gestores CAE por rol
/// y ningún Operador Delegado.
/// </para>
/// </summary>
public class ClientesListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado y QuickGrid, que importan módulos JS.</summary>
    public ClientesListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public List<ClienteListaDto> Almacen { get; } = [];
        public List<string> ErroresDeLote { get; } = [];
        public int? EliminadosForzados { get; set; }

        /// <summary>El lote devuelve qué ids cayeron, como el handler real (los tests anteriores a «Deshacer» solo fijan el recuento).</summary>
        public bool LoteDevuelveIds { get; set; }

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

        /// <summary>Por defecto, un usuario mono-Tenant: sin selector ni cabecera de empresa gestionada.</summary>
        public List<ClienteAutorizadoDto> Autorizados { get; } = [new(Guid.NewGuid(), "Propia", EsOrigen: true)];

        /// <summary>Si se fija, la respuesta de la lista de Tenants autorizados espera a esta tarea (mediador asíncrono).</summary>
        public Task? RetenerAutorizados { get; set; }

        /// <summary>El token con el que la página pidió la lista de Tenants autorizados (solo con <see cref="RetenerAutorizados"/>).</summary>
        public CancellationToken? TokenDeAutorizados { get; private set; }

        /// <summary>Si es cierto, la respuesta retenida ignora el token: simula una resolución que vuelve normal tras cancelarse.</summary>
        public bool IgnorarCancelacionDeAutorizados { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);

            if (request is ObtenerClientesAutorizadosQuery && RetenerAutorizados is { } espera)
            {
                TokenDeAutorizados = cancellationToken;
                // Un mediador real lanza al cancelarse; uno que "vuelve" con el contexto por defecto es el caso
                // que la guarda de Dispose de la página debe cubrir por sí sola.
                await (IgnorarCancelacionDeAutorizados ? espera : espera.WaitAsync(cancellationToken));
            }

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

                case ObtenerClientesAutorizadosQuery:
                    return (IReadOnlyList<ClienteAutorizadoDto>)Autorizados.ToList();

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
                    var idsBorrados = Almacen.Where(c => lote.Ids.Contains(c.Id)).Select(c => c.Id).ToList();
                    var borrados = Almacen.RemoveAll(c => lote.Ids.Contains(c.Id));
                    return Result.Exito(new ResultadoEliminacionLoteDto(EliminadosForzados ?? borrados, ErroresDeLote, LoteDevuelveIds ? idsBorrados : null));

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

        /// <summary>Mismo tope que <c>limiteCandidatosConFiltroCalculado</c> del handler.</summary>
        public const int LimiteCandidatosConFiltroDeEstado = 2000;

        /// <summary>
        /// Filtra, ordena y pagina en el mismo orden de pasos que
        /// <c>ObtenerClientesQueryHandler</c>: primero los filtros que el handler
        /// resuelve en SQL (búsqueda, críticos, ejecutivo), luego el orden con su
        /// desempate por Id y, solo si se filtra por estado documental, el tope
        /// de <see cref="LimiteCandidatosConFiltroDeEstado"/> candidatos ANTES de
        /// filtrar por estado: el total con ese filtro es el de los que
        /// coinciden entre esos candidatos, no en toda la cartera.
        /// </summary>
        public ResultadoPaginado<ClienteListaDto> Filtrar(ObtenerClientesQuery q)
        {
            var sinEstado = Almacen
                .Where(c => string.IsNullOrWhiteSpace(q.Busqueda)
                    || c.RazonSocial.ToUpperInvariant().Contains(q.Busqueda.ToUpperInvariant()))
                .Where(c => q.SoloCriticos != true || c.EsCritico)
                .Where(c => q.EjecutivoUsuarioId is null || c.EjecutivoUsuarioId == q.EjecutivoUsuarioId)
                .ToList();

            IEnumerable<ClienteListaDto> ordenados = Ordenar(sinEstado, q.OrdenarPor, q.Descendente).ThenBy(c => c.Id);

            // Orden por Estado documental (el de por defecto de la pantalla desde el rediseño de
            // listados, fase 1): como el handler, con OrderBy estable (la razón social queda como
            // desempate) y sobre la cartera entera; solo con filtro de estado hereda su tope.
            var ordenarPorEstado = q.OrdenarPor == nameof(ClienteListaDto.EstadoDocumentalPeor);
            // Como el handler: los estados de la franja (EstadosDocumentales) y el de un solo valor se suman en
            // un mismo conjunto, y pasa el Cliente empresarial que cumpla CUALQUIERA de ellos.
            var estadosPedidos = (q.EstadosDocumentales ?? []).ToHashSet();
            if (q.EstadoDocumental is { } unEstado)
                estadosPedidos.Add(unEstado);

            if (ordenarPorEstado)
            {
                var candidatos = estadosPedidos.Count == 0
                    ? ordenados.ToList()
                    : ordenados.Take(LimiteCandidatosConFiltroDeEstado).ToList();
                ordenados = q.Descendente
                    ? candidatos.OrderByDescending(Prioridad).ToList()
                    : candidatos.OrderBy(Prioridad).ToList();
            }

            // Vigente es el centinela de «sin ninguna alerta abierta» (botón «Sin incidencias»).
            var coincidentes = estadosPedidos.Count == 0
                ? ordenados.ToList()
                : ordenados
                    .Take(LimiteCandidatosConFiltroDeEstado)
                    .Where(c => c.EstadoDocumentalPeor is null
                        ? estadosPedidos.Contains(EstadoDocumento.Vigente)
                        : Presentes(c).Overlaps(estadosPedidos))
                    .ToList();

            var pagina = coincidentes
                .Skip((q.Pagina - 1) * q.TamanoPagina)
                .Take(q.TamanoPagina)
                .ToList();

            return new ResultadoPaginado<ClienteListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina)
            {
                RecuentosPorEstado = q.ConRecuentosPorEstado ? ContarPorEstado(sinEstado) : null,
                TotalSinFiltroDeEstado = q.ConRecuentosPorEstado ? sinEstado.Count : null
            };
        }

        /// <summary>
        /// Las cifras de la franja, como <c>ContarPorEstadoAsync</c> del handler: con los demás filtros y SIN el
        /// de estado; un estado cuenta a quien tiene ALGUNA alerta en él (por eso no suman el total) y la clave
        /// conjunta de «Por vencer» cuenta una vez a quien tiene urgentes y próximos.
        /// </summary>
        private Dictionary<string, int> ContarPorEstado(List<ClienteListaDto> sinEstado)
        {
            var presentes = sinEstado.Where(c => c.EstadoDocumentalPeor is not null).Select(Presentes).ToList();
            return new Dictionary<string, int>
            {
                [nameof(EstadoDocumento.Vencido)] = presentes.Count(p => p.Contains(EstadoDocumento.Vencido)),
                [nameof(EstadoDocumento.Faltante)] = presentes.Count(p => p.Contains(EstadoDocumento.Faltante)),
                [nameof(EstadoDocumento.Urgente)] = presentes.Count(p => p.Contains(EstadoDocumento.Urgente)),
                [nameof(EstadoDocumento.Proximo)] = presentes.Count(p => p.Contains(EstadoDocumento.Proximo)),
                [ObtenerClientesQuery.ClavePorVencer] = presentes.Count(p => p.Contains(EstadoDocumento.Urgente) || p.Contains(EstadoDocumento.Proximo)),
                [nameof(EstadoDocumento.Vigente)] = sinEstado.Count - presentes.Count
            };
        }

        /// <summary>Orden de gravedad del handler (decisión del 2026-10-03): Vencido antes que Faltante.</summary>
        private static int Prioridad(ClienteListaDto c) => (c.EstadoDocumentalPeor ?? EstadoDocumento.Vigente) switch
        {
            EstadoDocumento.Vencido => 0,
            EstadoDocumento.Faltante => 1,
            EstadoDocumento.Urgente => 2,
            EstadoDocumento.Proximo => 3,
            _ => 4
        };

        private HashSet<EstadoDocumento> Presentes(ClienteListaDto c) =>
            EstadosPresentes.TryGetValue(c.Id, out var presentes)
                ? presentes
                : c.EstadoDocumentalPeor is { } peor ? [peor] : [];

        /// <summary>
        /// Misma lista blanca que el handler; cualquier otro nombre (o ninguno)
        /// cae en su orden por defecto, por razón social ascendente. El
        /// desempate final por Id lo pone <see cref="Filtrar"/>, como el
        /// handler. Dos salvedades: se compara con <see cref="Guid.CompareTo(Guid)"/>,
        /// que no ordena igual que el <c>uuid</c> de PostgreSQL para Ids
        /// arbitrarios (el test del desempate usa Ids en los que ambos
        /// coinciden), y con Ids aleatorios un test que mirase posiciones de
        /// filas EMPATADAS cambiaría entre ejecuciones: ninguno lo hace.
        /// </summary>
        private static IOrderedEnumerable<ClienteListaDto> Ordenar(List<ClienteListaDto> filas, string? ordenarPor, bool descendente) =>
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
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Tenant del arnés cuando hay Gestores CAE; sin ellos no hay tenant resuelto.</summary>
    private static readonly Guid TenantDelArnes = Guid.Parse("0f1e2d3c-4b5a-4968-8776-a5b4c3d2e1f0");

    private sealed class TenantActualFalso(Guid? tenantId = null) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    /// <summary>
    /// Con tenant resuelto, el directorio pregunta por los Operadores Delegados
    /// del tenant con <c>ToDictionaryAsync</c>: aquí no hay ninguno, y las dos
    /// colecciones van envueltas para que EF acepte recorrerlas en asíncrono.
    /// </summary>
    private sealed class TenantsQueryContextSinDelegaciones : ITenantsQueryContext
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("El directorio solo consulta delegaciones y asignaciones; si esto salta, cambió el camino.");

        public IQueryable<Tenant> Tenants => throw NoDeberia();
        public IQueryable<DelegacionTenant> DelegacionesTenant => new ConsultaAsincrona<DelegacionTenant>(Array.Empty<DelegacionTenant>().AsQueryable());
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => new ConsultaAsincrona<AsignacionOperadorDelegado>(Array.Empty<AsignacionOperadorDelegado>().AsQueryable());
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw NoDeberia();
    }

    /// <summary>
    /// <c>IQueryable</c> en memoria que además es <see cref="IAsyncEnumerable{T}"/>,
    /// que es lo único que piden los operadores asíncronos de EF que usa el
    /// directorio (mismo patrón que <c>TestAsyncQueryable</c> de Application.Tests).
    /// </summary>
    private sealed class ConsultaAsincrona<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
    {
        public Type ElementType => interna.ElementType;
        public System.Linq.Expressions.Expression Expression => interna.Expression;
        public IQueryProvider Provider { get; } = new ProveedorAsincrono(interna.Provider);

        public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => interna.GetEnumerator();

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            new EnumeradorAsincrono<T>(interna.GetEnumerator());
    }

    private sealed class ProveedorAsincrono(IQueryProvider interno) : IQueryProvider
    {
        public IQueryable CreateQuery(System.Linq.Expressions.Expression expression) =>
            throw new NotSupportedException("El directorio solo compone consultas tipadas.");

        public IQueryable<TElement> CreateQuery<TElement>(System.Linq.Expressions.Expression expression) =>
            new ConsultaAsincrona<TElement>(interno.CreateQuery<TElement>(expression));

        public object? Execute(System.Linq.Expressions.Expression expression) => interno.Execute(expression);

        public TResult Execute<TResult>(System.Linq.Expressions.Expression expression) => interno.Execute<TResult>(expression);
    }

    private sealed class EnumeradorAsincrono<T>(IEnumerator<T> interno) : IAsyncEnumerator<T>
    {
        public T Current => interno.Current;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());

        public ValueTask DisposeAsync()
        {
            interno.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Almacén que solo sabe una cosa: quién tiene el rol Gestor CAE. Es lo que
    /// usa <c>ObtenerVisiblesEnRolAsync</c> (vía <c>GetUsersInRoleAsync</c>);
    /// todo lo demás sigue lanzando.
    /// </summary>
    private sealed class AlmacenGestoresCae(IReadOnlyList<ApplicationUser> gestores)
        : AlmacenUsuariosQueNadieDebeTocar, IUserRoleStore<ApplicationUser>
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Este almacén solo lista Gestores CAE por rol; si esto salta, cambió el camino.");

        public Task<IList<ApplicationUser>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken) =>
            Task.FromResult<IList<ApplicationUser>>(roleName == Roles.GestorCae ? gestores.ToList() : []);

        public Task AddToRoleAsync(ApplicationUser user, string roleName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task RemoveFromRoleAsync(ApplicationUser user, string roleName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<bool> IsInRoleAsync(ApplicationUser user, string roleName, CancellationToken cancellationToken) => throw NoDeberia();
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

    private class AlmacenUsuariosQueNadieDebeTocar : IUserStore<ApplicationUser>
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

    /// <param name="gestores">
    /// Sin ninguno, el camino de <see cref="ClientesVacioPorFiltroTests"/>: sin
    /// tenant, vacío y sin consultar. Con alguno, tenant resuelto y esos
    /// Gestores CAE como únicos visibles en el rol.
    /// </param>
    private static DirectorioUsuariosTenant CrearDirectorio(IReadOnlyList<ApplicationUser> gestores)
    {
        var conGestores = gestores.Count > 0;
        var tenantActual = new TenantActualFalso(conGestores ? TenantDelArnes : null);
        var identidad = new CaeManagerDbContext(
            new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            DataProtectionProvider.Create(nameof(ClientesListaGen2Tests)),
            tenantActual);

        var userManager = new UserManager<ApplicationUser>(
            conGestores ? new AlmacenGestoresCae(gestores) : new AlmacenUsuariosQueNadieDebeTocar(),
            null!, null!, null!, null!, null!, null!, null!, null!);

        return new DirectorioUsuariosTenant(
            userManager,
            conGestores ? new TenantsQueryContextSinDelegaciones() : new TenantsQueryContextQueNadieDebeTocar(),
            tenantActual, new PuertaAccesoDatos(), identidad);
    }

    private SeleccionEmpresaGestionadaDePrueba Seleccion { get; set; } = new();

    private void Registrar(MediatorFalso mediador, string rol = Roles.Administrador, IReadOnlyList<ApplicationUser>? gestores = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => Seleccion);
        // AvisoCambiosSinGuardar (P1-E2b) pinta sus textos con IStringLocalizer<TextosComunes>.
        Services.AddLocalization();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<AuthenticationStateProvider>(_ => new AutenticacionFalsa(rol));
        Services.AddAuthorizationCore();
        Services.AddScoped<IAuthorizationService, AutorizacionPorRoles>();
        Services.AddCascadingAuthenticationState();
        Services.AddScoped<IValidator<CrearClienteCommand>>(_ => new InlineValidator<CrearClienteCommand>());
        Services.AddScoped(_ => CrearDirectorio(gestores ?? []));
        // ClientePreviewDrawer (pieza 6 del patrón de lista) inyecta el
        // UserManager; solo lo usa al abrirse, para resolver el nombre del
        // Gestor CAE del resumen.
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            gestores is { Count: > 0 } ? new AlmacenGestoresCae(gestores) : new AlmacenUsuariosQueNadieDebeTocar(),
            null!, null!, null!, null!, null!, null!, null!, null!));
        Services.AddScoped<PuertaAccesoDatos>();
    }

    /// <param name="url">Ruta relativa con la que se abre la página (p. ej. <c>clientes?critico=true</c>).</param>
    /// <param name="gestores">Gestores CAE que ofrece el desplegable de Ejecutivo; ver <see cref="CrearDirectorio"/>.</param>
    private IRenderedComponent<Clientes> Renderizar(
        MediatorFalso mediador, string url = "clientes", string rol = Roles.Administrador,
        IReadOnlyList<ApplicationUser>? gestores = null)
    {
        Registrar(mediador, rol, gestores);
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

    /// <summary>
    /// El disparador de la pastilla de filtro de ese nombre (rediseño de listados, fase 1): su
    /// nombre accesible es la etiqueta sin valor aplicado, o «etiqueta: opción» con valor.
    /// </summary>
    private static IElement Pastilla(IRenderedComponent<Clientes> cut, string etiqueta) =>
        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla")
            .Single(b => b.GetAttribute("aria-label") is { } nombre && (nombre == etiqueta || nombre.StartsWith(etiqueta + ": ", StringComparison.Ordinal)));

    /// <summary>Abre la pastilla y pulsa la opción (menuitemradio) con ese texto.</summary>
    private static async Task ElegirEnLaPastilla(IRenderedComponent<Clientes> cut, string etiqueta, string opcion)
    {
        await Pastilla(cut, etiqueta).ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]")
            .Single(i => i.TextContent.Trim() == opcion).ClickAsync(new MouseEventArgs());
    }

    /// <summary>
    /// Marca (o desmarca) un botón de la franja de estado por su rótulo. El estado ya no es una pastilla:
    /// «Vencidos», «Pendientes», «Por vencer» y «Sin incidencias» se marcan por separado y se suman.
    /// </summary>
    private static Task AlternarEnLaFranja(IRenderedComponent<Clientes> cut, string rotulo) =>
        cut.BotonDeFranja(rotulo).ClickAsync(new MouseEventArgs());

    /// <summary>Abre «Más filtros» y pulsa el ítem con ese texto (un filtro guardado o «Guardar filtro»).</summary>
    private static async Task PulsarEnMasFiltros(IRenderedComponent<Clientes> cut, string item)
    {
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.FindAll(".barra-filtros-pastillas [role=menuitem]")
            .Single(i => i.TextContent.Trim() == item).ClickAsync(new MouseEventArgs());
    }

    private static Task AlternarSeleccionMultiple(IRenderedComponent<Clientes> cut) =>
        cut.Find("header.cabecera-pagina button.cabecera-listado-icono").ClickAsync(new MouseEventArgs());

    private static IElement Buscador(IRenderedComponent<Clientes> cut) =>
        cut.Find(".barra-filtros-pastillas input[type=text]");

    private static ApplicationUser GestorCae(string nombreCompleto) =>
        new() { Id = Guid.NewGuid(), NombreCompleto = nombreCompleto, Email = "gestor@arnes.invalid", TenantId = TenantDelArnes };

    private static List<string> TextosDeLosChips(IRenderedComponent<Clientes> cut) =>
        cut.FindAll(".barra-filtros-pastillas .chip-filtro").Select(c => c.TextContent.Trim()).ToList();

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

    /// <summary>
    /// Rediseño de listados, fase 1: cabecera de una línea —título y contador, ☑ «Selección
    /// múltiple», el menú «⋯» y UNA primaria—, sin antetítulo y sin el rótulo de la empresa
    /// gestionada.
    /// </summary>
    [Fact]
    public void La_cabecera_es_de_una_linea_con_contador_seleccion_menu_y_una_primaria()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L.") } });

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker").Should().BeNull("el grupo del menú ya está en las migas");
        cabecera.QuerySelector("h1.titulo-pagina")!.TextContent.Trim().Should().Be("Clientes empresariales");
        cabecera.QuerySelector(".cabecera-listado-contador")!.TextContent.Trim().Should().Be("2");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();

        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.QuerySelectorAll("a").Should().BeEmpty("Exportar a Excel vive ahora dentro del «⋯»");
        acciones.QuerySelectorAll("button").Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Equal("Selección múltiple", "Atajos de teclado", "Más acciones", "+ Nuevo Cliente empresarial");
    }

    [Fact]
    public async Task El_menu_de_la_cabecera_exporta_guarda_las_importaciones_del_Administrador_y_el_alta_guiada()
    {
        var cut = Renderizar(new MediatorFalso());

        await cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Single().ClickAsync(new MouseEventArgs());

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar a Excel", "Importar Clientes empresariales", "Importación combinada", "Alta guiada");
        cut.Find("header.cabecera-pagina a.menu-acciones-item").GetAttribute("href").Should().Be("/clientes/exportar.xlsx",
            "la exportación es una descarga del servidor: un enlace, no una navegación interna");
        await cut.FindAll("header.cabecera-pagina .menu-acciones-item").Single(i => i.TextContent.Trim() == "Alta guiada")
            .ClickAsync(new MouseEventArgs());
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath.Should().Be("/clientes/alta-guiada");
    }

    [Fact]
    public async Task El_menu_de_la_cabecera_de_un_Gestor_CAE_no_ofrece_las_importaciones_del_Administrador()
    {
        var cut = Renderizar(new MediatorFalso(), rol: Roles.GestorCae);

        await cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Single().ClickAsync(new MouseEventArgs());

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar a Excel", "Alta guiada");
    }

    [Fact]
    public async Task Un_rol_de_Consulta_solo_puede_exportar_y_no_ve_el_alta()
    {
        var cut = Renderizar(new MediatorFalso(), rol: Roles.Consulta);

        await cut.FindAll("header.cabecera-pagina .menu-acciones-disparador").Single().ClickAsync(new MouseEventArgs());

        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Exportar a Excel");
        cut.Find("header.cabecera-pagina .acciones-cabecera").TextContent.Should().NotContain("Nuevo Cliente empresarial");
    }

    [Fact]
    public async Task Nuevo_cliente_de_la_cabecera_abre_el_drawer_de_alta()
    {
        var cut = Renderizar(new MediatorFalso());
        cut.FindAll(".drawer-panel").Should().BeEmpty("punto de partida: el drawer está cerrado");

        await cut.FindAll("header.cabecera-pagina .acciones-cabecera button").Single(b => b.TextContent.Trim() == "+ Nuevo Cliente empresarial").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo Cliente empresarial");
    }

    [Fact]
    public void El_buscador_es_Filtrar_esta_pantalla_y_no_promete_buscar_por_alias_ni_CIF()
    {
        var cut = Renderizar(new MediatorFalso());

        var buscador = Buscador(cut);
        buscador.GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: nombre",
            "ObtenerClientesQuery solo busca en la razón social y el DTO no tiene alias; los E2E usan este texto");
        buscador.GetAttribute("aria-label").Should().Be("Filtrar esta pantalla");
        buscador.HasAttribute("data-filtro-pantalla").Should().BeTrue("es lo que enfoca la tecla f (atajos-lista.js)");
        cut.Find(".barra-filtros-pastillas kbd.barra-filtros-tecla").TextContent.Trim().Should().Be("F");
        cut.Markup.Should().NotContain("alias o CIF");
    }

    // --------------------------------------------------------- Conteo y filtros

    /// <summary>Sin filtros el número lo dice el contador de la cabecera; el resumen de la barra no se repite.</summary>
    [Fact]
    public void Sin_filtros_el_total_lo_dice_el_contador_de_la_cabecera()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L."), Cliente("Grúas Aldapa S.L.") } });

        cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("3");
        cut.FindAll(".conteo-clientes").Should().BeEmpty();
    }

    [Fact]
    public void Las_pastillas_son_Gestor_CAE_y_Criticidad_seguidas_de_Mas_filtros_y_el_Estado_va_en_la_franja()
    {
        var cut = Renderizar(new MediatorFalso());

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Select(b => b.GetAttribute("aria-label"))
            .Should().Equal("Gestor CAE", "Criticidad", "Más filtros");
        cut.RotulosDeFranja().Should().Equal("Todos", "Vencidos", "Pendientes", "Por vencer", "Sin incidencias");
        cut.MarcadosEnFranja().Should().Equal(["Todos"], "sin filtro de estado, el marcado es «Todos»");
    }

    /// <summary>
    /// Cada botón de la franja dice cuántos Clientes empresariales quedarían al marcarlo solo. Como el filtro
    /// pregunta «hay alguna alerta», uno con vencidos y urgentes cuenta en los dos botones y las cifras no suman
    /// el total: «Todos» enseña el total de la consulta y no la suma. «Por vencer» cuenta una vez a quien tiene
    /// urgentes Y próximos (clave conjunta), no dos.
    /// </summary>
    [Fact]
    public void La_franja_cuenta_por_estado_sin_sumar_dos_veces_y_Todos_dice_el_total()
    {
        var conDeTodo = Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 2);
        var porVencer = Cliente("Montajes Ebro S.L.", peor: EstadoDocumento.Urgente, cantidad: 1);
        var mediador = new MediatorFalso
        {
            Almacen = { conDeTodo, porVencer, Cliente("Grúas Aldapa S.L."), Cliente("Talleres Berriz Coop.") }
        };
        mediador.EstadosPresentes[conDeTodo.Id] = [EstadoDocumento.Vencido, EstadoDocumento.Urgente];
        mediador.EstadosPresentes[porVencer.Id] = [EstadoDocumento.Urgente, EstadoDocumento.Proximo];
        var cut = Renderizar(mediador);

        UltimaConsulta(mediador).ConRecuentosPorEstado.Should().BeTrue("sin pedirlos, la franja no tendría cifras");
        cut.WaitForAssertion(() => cut.BotonDeFranja("Todos").RecuentoDeFranja().Should().Be(4));
        cut.BotonDeFranja("Vencidos").RecuentoDeFranja().Should().Be(1);
        cut.BotonDeFranja("Pendientes").RecuentoDeFranja().Should().Be(0);
        cut.BotonDeFranja("Pendientes").ClassList.Should().Contain("franja-estado-boton-vacio");
        cut.BotonDeFranja("Por vencer").RecuentoDeFranja().Should().Be(2,
            "Montajes tiene urgentes y próximos: sumar las dos claves daría 3");
        cut.BotonDeFranja("Sin incidencias").RecuentoDeFranja().Should().Be(2);
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
        cut.Find(".conteo-clientes").TextContent.Trim().Should().Be("1 Cliente empresarial con estos filtros");
        NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]);
    }

    [Fact]
    public async Task Escribir_en_el_buscador_manda_la_busqueda_la_escribe_en_la_url_y_recorta_las_filas()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A."), Cliente("Montajes Ebro S.L.") } };
        var cut = Renderizar(mediador);

        // InputAsync espera al debounce de CampoTexto (300 ms) y a su recarga.
        await Buscador(cut).InputAsync(new ChangeEventArgs { Value = "refri" });

        UltimaConsulta(mediador).Busqueda.Should().Be("refri");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=refri");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
    }

    /// <summary>
    /// «Pendientes» pregunta si HAY algún pendiente (Faltante), no si el peor estado es ese: Montajes tiene
    /// vencidos (peor) y además un faltante, y tiene que salir. El estado viaja en la consulta como lista y en
    /// la URL como <c>estado=</c>, y se ve marcado en la franja: ya no pinta chip.
    /// </summary>
    [Fact]
    public async Task El_filtro_de_estado_documental_viaja_en_la_consulta_y_en_la_url_y_se_ve_marcado_en_la_franja()
    {
        var montajes = Cliente("Montajes Ebro S.L.", peor: EstadoDocumento.Vencido, cantidad: 1);
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Faltante, cantidad: 12), montajes, Cliente("Grúas Aldapa S.L.") }
        };
        mediador.EstadosPresentes[montajes.Id] = [EstadoDocumento.Faltante, EstadoDocumento.Vencido];
        var cut = Renderizar(mediador);

        await AlternarEnLaFranja(cut, "Pendientes");

        UltimaConsulta(mediador).EstadosDocumentales.Should().Equal(EstadoDocumento.Faltante);
        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull("la página manda la lista, no el filtro de un solo estado");
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith("estado=Faltante");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Montajes Ebro S.L.", "Refrielectric S.A."]));
        cut.MarcadosEnFranja().Should().Equal("Pendientes");
        TextosDeLosChips(cut).Should().BeEmpty("el estado se ve en la franja, no como chip");
    }

    /// <summary>
    /// La franja admite varios estados: marcar un segundo botón lo SUMA al primero (pasa quien cumpla
    /// cualquiera), «Por vencer» manda Urgente y Próximo a la vez, y «Todos» borra la selección de la consulta
    /// y de la URL.
    /// </summary>
    [Fact]
    public async Task Marcar_dos_botones_de_la_franja_suma_sus_estados_y_Todos_los_borra()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 12),
                Cliente("Montajes Ebro S.L.", peor: EstadoDocumento.Proximo, cantidad: 1),
                Cliente("Grúas Aldapa S.L.", peor: EstadoDocumento.Faltante, cantidad: 1),
                Cliente("Talleres Berriz Coop.")
            }
        };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await AlternarEnLaFranja(cut, "Vencidos");
        await AlternarEnLaFranja(cut, "Por vencer");

        UltimaConsulta(mediador).EstadosDocumentales
            .Should().Equal(EstadoDocumento.Vencido, EstadoDocumento.Urgente, EstadoDocumento.Proximo);
        Uri.UnescapeDataString(navegacion.Uri).Should().EndWith("estado=Vencido,Urgente,Proximo");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A.", "Montajes Ebro S.L."]));
        cut.MarcadosEnFranja().Should().Equal("Vencidos", "Por vencer");

        await AlternarEnLaFranja(cut, "Todos");

        UltimaConsulta(mediador).EstadosDocumentales.Should().BeNull();
        navegacion.Uri.Should().NotContain("estado=");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(4));
        cut.MarcadosEnFranja().Should().Equal("Todos");
    }

    /// <summary>
    /// El estado llega también de la URL (enlace compartido, vuelta atrás): se marca en la franja y filtra la
    /// primera consulta. Un valor que ningún botón conoce se descarta en vez de filtrar por algo invisible.
    /// </summary>
    [Fact]
    public void El_estado_de_la_url_marca_la_franja_y_filtra_y_lo_desconocido_se_descarta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 12), Cliente("Montajes Ebro S.L.") }
        };

        var cut = Renderizar(mediador, "clientes?estado=Vencido,Inventado");

        UltimaConsulta(mediador).EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
        cut.MarcadosEnFranja().Should().Equal("Vencidos");
    }

    [Fact]
    public async Task El_filtro_de_Gestor_CAE_viaja_en_la_consulta_y_la_pastilla_dice_quien()
    {
        var gestora = GestorCae("Marta Ibarra");
        var deLaGestora = Cliente("Refrielectric S.A.") with { EjecutivoUsuarioId = gestora.Id };
        var mediador = new MediatorFalso { Almacen = { deLaGestora, Cliente("Montajes Ebro S.L.") } };
        var cut = Renderizar(mediador, gestores: [gestora]);

        await ElegirEnLaPastilla(cut, "Gestor CAE", "Marta Ibarra");

        UltimaConsulta(mediador).EjecutivoUsuarioId.Should().Be(gestora.Id);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
        var pastilla = Pastilla(cut, "Gestor CAE");
        pastilla.GetAttribute("aria-label").Should().Be("Gestor CAE: Marta Ibarra");
        pastilla.ClassList.Should().Contain("menu-acciones-disparador-activa");
    }

    /// <summary>
    /// Elegir «Todos» en la pastilla quita el filtro, igual que la ✕ de su chip: es la opción
    /// marcada mientras no hay valor.
    /// </summary>
    [Fact]
    public async Task Todos_en_la_pastilla_quita_el_filtro()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.", critico: true), Cliente("Montajes Ebro S.L.") } };
        var cut = Renderizar(mediador, "clientes?critico=true");
        UltimaConsulta(mediador).SoloCriticos.Should().BeTrue("es el punto de partida de este caso");

        await ElegirEnLaPastilla(cut, "Criticidad", "Todos");

        UltimaConsulta(mediador).SoloCriticos.Should().BeNull();
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("critico");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(2));
        Pastilla(cut, "Criticidad").GetAttribute("aria-label").Should().Be("Criticidad");
    }

    /// <summary>
    /// «Limpiar todo» (mockup) y «Quitar los filtros» (estado vacío) son el
    /// mismo LimpiarFiltrosAsync: quitan los cuatro filtros y los dos de la URL.
    /// Si dejara <c>critico</c> en la URL, la siguiente pasada de parámetros lo
    /// devolvería.
    /// </summary>
    // ------------------------- Gestor CAE y estado documental viajan en la URL (T20)

    [Fact]
    public async Task Elegir_Gestor_CAE_y_estado_documental_los_escribe_en_la_url()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 2) with { EjecutivoUsuarioId = marta.Id } }
        };
        var cut = Renderizar(mediador, gestores: [marta]);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await ElegirEnLaPastilla(cut, "Gestor CAE", "Marta Ibarra");
        // El estado se marca en la franja, que sustituyó a la pastilla «Estado».
        await AlternarEnLaFranja(cut, "Vencidos");

        navegacion.Uri.Should().Contain($"gestor={marta.Id}").And.Contain("estado=Vencido");
        var consulta = UltimaConsulta(mediador);
        consulta.EjecutivoUsuarioId.Should().Be(marta.Id, "escribir el segundo filtro en la URL no puede devolver el primero a vacío");
        consulta.EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);
    }

    /// <summary>Recargar o compartir el enlace reproduce la vista: los dos filtros salen de la URL.</summary>
    [Fact]
    public void Un_enlace_con_Gestor_CAE_y_estado_documental_filtra_la_consulta_y_los_deja_a_la_vista()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 2) with { EjecutivoUsuarioId = marta.Id } }
        };
        var cut = Renderizar(mediador, $"clientes?gestor={marta.Id}&estado=Vencido", gestores: [marta]);

        var consulta = UltimaConsulta(mediador);
        consulta.EjecutivoUsuarioId.Should().Be(marta.Id);
        consulta.EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);
        // El Gestor CAE se ve en su chip; el estado, marcado en la franja (ya no tiene chip).
        cut.WaitForAssertion(() => TextosDeLosChips(cut).Should().Contain(t => t.StartsWith("Gestor CAE: Marta Ibarra")));
        cut.MarcadosEnFranja().Should().Equal("Vencidos");
    }

    [Fact]
    public void Valores_de_la_url_que_no_son_un_Id_ni_un_estado_conocido_no_filtran()
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } };
        var cut = Renderizar(mediador, "clientes?gestor=marta&estado=999");

        var consulta = UltimaConsulta(mediador);
        consulta.EjecutivoUsuarioId.Should().BeNull();
        consulta.EstadosDocumentales.Should().BeNull("999 se convierte a un número de enum que no existe: no es un estado");
        TextosDeLosChips(cut).Should().BeEmpty();
    }

    /// <summary>Un Id en la URL no es autoridad: solo filtra un Gestor CAE que el directorio visible ofrece.</summary>
    [Fact]
    public void Un_Gestor_CAE_de_la_url_que_el_directorio_visible_no_ofrece_no_filtra()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } };
        var cut = Renderizar(mediador, $"clientes?gestor={Guid.NewGuid()}", gestores: [marta]);

        UltimaConsulta(mediador).EjecutivoUsuarioId.Should().BeNull();
        TextosDeLosChips(cut).Should().BeEmpty("no hay chip «Gestor CAE: —»");
    }

    [Fact]
    public async Task Un_filtro_guardado_con_un_estado_documental_que_no_existe_no_filtra_por_estado()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Antiguo",
            JsonSerializer.Serialize(new { Busqueda = "Refri", EstadoDocumental = "99" }), DateTime.UtcNow);
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") }, FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await PulsarEnMasFiltros(cut, filtro.Nombre);

        UltimaConsulta(mediador).EstadoDocumental.Should().BeNull("99 se convierte a un número de enum que no existe");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Refri").And.NotContain("estado=");
    }

    [Fact]
    public async Task Limpiar_todo_quita_tambien_Gestor_CAE_y_estado_documental_de_la_url()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 2) with { EjecutivoUsuarioId = marta.Id } }
        };
        var cut = Renderizar(mediador, $"clientes?gestor={marta.Id}&estado=Vencido", gestores: [marta]);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        await cut.Find("button.limpiar-filtros-barra").ClickAsync(new MouseEventArgs());

        navegacion.Uri.Should().NotContain("gestor=").And.NotContain("estado=",
            "OnParametersSetAsync los repondría desde la URL en la siguiente pasada de parámetros");
        var consulta = UltimaConsulta(mediador);
        consulta.EjecutivoUsuarioId.Should().BeNull();
        consulta.EstadoDocumental.Should().BeNull();
        cut.WaitForAssertion(() => cut.FindAll(".chip-filtro").Should().BeEmpty());
    }

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
        cut.WaitForAssertion(() => cut.Find(".cabecera-listado-contador").TextContent.Trim().Should().Be("2"));
        cut.FindAll(".conteo-clientes").Should().BeEmpty("sin filtros el resumen de la barra no se repite");
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
        cut.Find(".paginador-texto").TextContent.Should().Contain("Página 2 de 2").And.Contain("25 Cliente(s) empresarial(es)");
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

    private static int ConsultasDeLista(MediatorFalso mediador) =>
        mediador.Enviadas.OfType<ObtenerClientesQuery>().Count();

    /// <summary>
    /// Cambiar el tamaño de página pide la página 1 del tamaño nuevo UNA vez.
    /// <c>SetCurrentPageIndexAsync</c> ya avisa a QuickGrid aunque la página no
    /// cambie, así que refrescar además la rejilla pedía lo mismo dos veces.
    /// Los 60 clientes son los mismos antes y después: con el total quieto, lo
    /// que se cuenta es lo que pide la página y no una repetición de QuickGrid.
    /// </summary>
    [Fact]
    public async Task Cambiar_el_tamano_de_pagina_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 60; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}"));
        var cut = Renderizar(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(50));
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "avisar a la paginación y refrescar la rejilla son dos formas de pedir lo mismo");
    }

    /// <summary>
    /// Cambiar un filtro recarga la lista UNA vez. El primer cambio solo sirve
    /// para asentar el total en 1: se mide el SEGUNDO, con el total quieto — si
    /// cambiara, QuickGrid volvería a pedir la misma página por su cuenta y el
    /// recuento mezclaría esa repetición con la consulta de la página. Con la
    /// franja un clic solo añade o quita estados, así que el segundo cambio que
    /// deja el total quieto es sumar «Por vencer» cuando nadie lo está: el filtro
    /// cambia (se comprueba en la consulta) y las filas son las mismas.
    /// </summary>
    [Fact]
    public async Task Cambiar_de_filtro_sin_cambiar_el_total_hace_una_sola_consulta()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 1), Cliente("Montajes Ebro S.L.") }
        };
        var cut = Renderizar(mediador);

        await AlternarEnLaFranja(cut, "Vencidos");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal("Refrielectric S.A."));
        var consultasAntes = ConsultasDeLista(mediador);

        await AlternarEnLaFranja(cut, "Por vencer");

        cut.WaitForAssertion(() => UltimaConsulta(mediador).EstadosDocumentales
            .Should().Equal(EstadoDocumento.Vencido, EstadoDocumento.Urgente, EstadoDocumento.Proximo));
        cut.WaitForAssertion(() => cut.MarcadosEnFranja().Should().Equal("Vencidos", "Por vencer"));
        NombresDeLasFilas(cut).Should().Equal("Refrielectric S.A.");
        (ConsultasDeLista(mediador) - consultasAntes).Should().Be(1,
            "los dos filtros devuelven un Cliente empresarial: la única consulta que cabe contar es la del filtro nuevo");
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

        await ElegirEnLaPastilla(cut, "Criticidad", "Solo críticos");
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Ningún Cliente empresarial con estos filtros"));

        await cut.InvokeAsync(() => respuestaVieja.SetResult(
            mediador.Filtrar(new ObtenerClientesQuery(null, null))));

        // Cualquier repintado posterior enseña el estado que dejó la respuesta
        // vieja; se fuerza uno para no depender de cuál llegue.
        cut.Render();

        cut.Markup.Should().Contain("Ningún Cliente empresarial con estos filtros",
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

        await cut.FindAll("header.cabecera-pagina .acciones-cabecera button").Single(b => b.TextContent.Trim() == "+ Nuevo Cliente empresarial").ClickAsync(new MouseEventArgs());
        var primero = cut.FindAll(".drawer-pie button.boton-espera-boton").Single() /* mientras guarda, su texto es «Guardando…» */.ClickAsync(new MouseEventArgs());
        var segundo = cut.FindAll(".drawer-pie button.boton-espera-boton").Single() /* mientras guarda, su texto es «Guardando…» */.ClickAsync(new MouseEventArgs());

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
        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo Cliente empresarial"));

        await cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == "Cancelar").ClickAsync(new MouseEventArgs());
        cut.FindAll(".drawer-panel").Should().BeEmpty();
        navegacion.Uri.Should().NotContain("accion=", "si se quedara, el siguiente «n» navegaría a la misma URL y no abriría nada");

        await cut.InvokeAsync(() => navegacion.NavigateTo("clientes?accion=crear"));
        cut.WaitForAssertion(() => cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo Cliente empresarial"));
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
        await ElegirEnLaPastilla(cut, "Criticidad", "Solo críticos");

        cut.FindAll("[role=dialog]").Should().BeEmpty("el modal ya se atendió y se cerró");
    }

    // ------------------------------------------------------------- Filas

    /// <summary>
    /// La celda de estado (EstadoFila): la pastilla dice el peor estado con el vocabulario único («Vencido»,
    /// «Por vencer»), el motivo de debajo dice cuántos documentos —concordado— sin repetir el estado, y quien
    /// no tiene alertas no lleva pastilla: «Sin incidencias» con punto verde.
    /// </summary>
    [Fact]
    public void El_estado_documental_dice_el_peor_estado_concuerda_el_recuento_debajo_y_explica_de_donde_sale()
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

        // Peor estado primero (orden por defecto): los dos vencidos, por razón social, y después el resto.
        var celdas = cut.FindAll("tbody .estado-fila");
        celdas.Select(c => (
                Pastilla: c.QuerySelector(".badge")?.TextContent.Trim(),
                Motivo: c.QuerySelector(".estado-fila-motivo")?.TextContent.Trim(),
                Correcto: c.QuerySelector("[data-pieza=estado-correcto]")?.TextContent.Trim()))
            .Should().Equal(
                ("Vencido", "1 documento", null),
                ("Vencido", "12 documentos", null),
                ("Por vencer", "9 documentos", null),
                (null, null, "Sin incidencias"));
        celdas[1].QuerySelector(".badge")!.GetAttribute("title").Should().Contain("trabajadores").And.NotContain("centros",
            "el agregado de ObtenerClientesQuery sale de las alertas de sus trabajadores; los centros no entran");
    }

    /// <summary>
    /// Rediseño de listados, fase 1: la columna «Crítico» desaparece y la situación crítica
    /// es una insignia «⚑ Crítico» junto al nombre, con texto visible (el punto de antes
    /// necesitaba un nombre accesible porque no lo tenía). Las filas no críticas no llevan nada.
    /// </summary>
    [Fact]
    public void Lo_critico_es_una_insignia_junto_al_nombre_y_no_una_columna()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Montajes Ebro S.L."), Cliente("Refrielectric S.A.", critico: true) } });

        cut.FindAll("thead th").Select(th => th.TextContent.Trim()).Should().NotContain("Crítico");
        cut.FindAll("tbody tr")[0].QuerySelector(".insignia-critico").Should().BeNull();
        var insignia = cut.FindAll("tbody tr")[1].QuerySelector(".insignia-critico")!;
        insignia.TextContent.Trim().Should().Be("Crítico");
        insignia.QuerySelector("svg").Should().NotBeNull("lleva el banderín");
        insignia.ParentElement!.QuerySelector(".enlace-nombre-fila")!.TextContent.Trim().Should().Be("Refrielectric S.A.",
            "la insignia va en la misma celda que el nombre");
    }

    /// <summary>
    /// Sin ningún orden elegido, la consulta pide el de Estado documental ascendente —el peor
    /// primero— y las filas con Faltante o Vencido se tintan de peligro, las Urgentes de aviso.
    /// El doble ordena como el handler, así que una pantalla que no mandara el orden pintaría las
    /// filas por razón social.
    /// </summary>
    [Fact]
    public void Por_defecto_el_peor_estado_va_primero_y_las_filas_en_riesgo_se_tintan()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Alfa Montajes S.L."),
                Cliente("Beta Frío S.A.", peor: EstadoDocumento.Urgente, cantidad: 1),
                Cliente("Gamma Grúas S.L.", peor: EstadoDocumento.Faltante, cantidad: 2),
                Cliente("Zeta Talleres Coop.", peor: EstadoDocumento.Vencido, cantidad: 3),
            }
        };
        var cut = Renderizar(mediador);

        var consulta = UltimaConsulta(mediador);
        consulta.OrdenarPor.Should().Be(nameof(ClienteListaDto.EstadoDocumentalPeor));
        consulta.Descendente.Should().BeFalse();
        // Orden de gravedad (decisión del 2026-10-03): Vencido antes que Faltante.
        NombresDeLasFilas(cut).Should().Equal(["Zeta Talleres Coop.", "Gamma Grúas S.L.", "Beta Frío S.A.", "Alfa Montajes S.L."]);
        // QuickGrid rellena la página con filas vacías: solo cuentan las que pintan un Cliente empresarial.
        cut.FindAll("tbody tr").Where(tr => tr.QuerySelector(".enlace-nombre-fila") is not null)
            .Select(tr => tr.ClassName ?? string.Empty).Should().Equal(
            "fila-tintada-peligro", "fila-tintada-peligro", "fila-tintada-aviso", string.Empty);
    }

    /// <summary>
    /// El foco de j/k se suma al tinte, no lo sustituye: la fila enfocada sigue diciendo que está
    /// en riesgo, y al irse el foco vuelve a ser solo tintada.
    /// </summary>
    [Fact]
    public async Task La_fila_enfocada_con_j_conserva_el_tinte_de_su_estado()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { Cliente("Alfa Montajes S.L."), Cliente("Zeta Talleres Coop.", peor: EstadoDocumento.Vencido, cantidad: 1) }
        });
        var atajos = cut.FindComponent<AtajosListaTeclado>();
        IEnumerable<string> Clases() => cut.FindAll("tbody tr").Where(tr => tr.QuerySelector(".enlace-nombre-fila") is not null)
            .Select(tr => tr.ClassName ?? string.Empty);

        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        Clases().Should().Equal("fila-enfocada fila-tintada-peligro", string.Empty);

        await cut.InvokeAsync(() => atajos.Instance.RecibirAtajo("j"));
        Clases().Should().Equal("fila-tintada-peligro", "fila-enfocada");
    }

    /// <summary>
    /// Con un tamaño de página mayor que el mínimo ya elegido, el paginador (y su selector de
    /// tamaño) se queda aunque un filtro deje el total por debajo de 20: si desapareciera, no
    /// habría forma de volver a 20 por página. Sin tamaño elegido, con 20 o menos no se pinta.
    /// </summary>
    [Fact]
    public async Task El_selector_de_tamano_sigue_ahi_si_se_eligio_uno_mayor_aunque_el_total_baje()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 25; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}", critico: i <= 3));
        var cut = Renderizar(mediador);
        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "50" });

        await ElegirEnLaPastilla(cut, "Criticidad", "Solo críticos");

        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().HaveCount(3));
        cut.FindAll(".paginador-tamano-select").Should().ContainSingle("el tamaño elegido (50) tiene que poder deshacerse");

        await cut.Find(".paginador-tamano-select").ChangeAsync(new ChangeEventArgs { Value = "20" });
        cut.WaitForAssertion(() => cut.FindAll(".paginador-tamano-select").Should().BeEmpty(
            "de vuelta al mínimo, con 3 resultados no hay nada que paginar"));
    }

    [Fact]
    public void Con_veinte_o_menos_y_el_tamano_minimo_no_hay_paginador()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= 20; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:00}"));

        var cut = Renderizar(mediador);

        NombresDeLasFilas(cut).Should().HaveCount(20);
        cut.FindAll(".paginador-simple").Should().BeEmpty();
    }

    /// <summary>La columna «Gestor CAE» pinta las iniciales de la persona delante de su nombre.</summary>
    [Fact]
    public void El_Gestor_CAE_de_la_fila_lleva_sus_iniciales()
    {
        var marta = GestorCae("Marta Ibarra");
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { Cliente("Montajes Ebro S.L."), Cliente("Refrielectric S.A.") with { EjecutivoUsuarioId = marta.Id } }
        }, gestores: [marta]);

        var iniciales = cut.FindAll("tbody .persona-identidad .avatar-usuario").Should().ContainSingle().Subject;
        iniciales.TextContent.Trim().Should().Be("MI");
        iniciales.ParentElement!.TextContent.Should().Contain("Marta Ibarra");
    }

    /// <summary>Si esa persona eligió un avatar del catálogo, la columna lo pinta en lugar de sus iniciales.</summary>
    [Fact]
    public void El_Gestor_CAE_de_la_fila_lleva_su_avatar_si_lo_eligio()
    {
        var marta = GestorCae("Marta Ibarra");
        marta.Avatar = "buho-ambar";
        var cut = Renderizar(new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.") with { EjecutivoUsuarioId = marta.Id } }
        }, gestores: [marta]);

        var avatar = cut.FindAll("tbody .persona-identidad .avatar-usuario").Should().ContainSingle().Subject;
        avatar.TextContent.Trim().Should().Be("🦉");
        avatar.ClassList.Should().Contain("avatar-usuario-tono-ambar");
        avatar.ParentElement!.TextContent.Should().Contain("Marta Ibarra");
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

    /// <summary>
    /// El nombre de la fila (pieza 5 del patrón de lista) es un botón en
    /// negrita que abre la vista previa lateral (pieza 6): no navega a la ficha
    /// ni abre el panel de 520 px.
    /// </summary>
    [Fact]
    public async Task El_nombre_de_la_fila_abre_la_vista_previa_lateral_sin_salir_de_la_lista()
    {
        var otro = Cliente("Aislamientos Nervión S.L.");
        var abierto = Cliente("Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Almacen = { otro, abierto } });
        cut.FindAll("aside.drawer-preview-cliente").Should().BeEmpty("punto de partida: la vista previa está cerrada");

        var nombre = cut.FindAll("tbody tr")[1].QuerySelector(".enlace-nombre-fila")!;
        nombre.TagName.Should().Be("BUTTON");
        nombre.ClassList.Should().Contain("nombre-fila-entidad");
        await nombre.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() =>
            cut.Find("aside.drawer-preview-cliente .nombre-cabecera-preview-cliente").TextContent.Trim()
                .Should().Be("Montajes Ebro S.L."));
        new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath.Should().Be("/clientes");
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse(
            "la vista previa lateral no es el Context Workspace");
    }

    [Fact]
    public async Task Abrir_ficha_360_del_pie_de_la_vista_previa_navega_a_la_pagina_y_la_cierra()
    {
        var abierto = Cliente("Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Almacen = { abierto } });
        await cut.Find("tbody tr .enlace-nombre-fila").ClickAsync(new MouseEventArgs());

        await cut.Find("aside.drawer-preview-cliente .pie-preview-cliente button").ClickAsync(new MouseEventArgs());

        new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath.Should().Be($"/clientes/{abierto.Id}");
        cut.FindAll("aside.drawer-preview-cliente").Should().BeEmpty("al abrir la ficha la vista previa se cierra");
    }

    [Fact]
    public async Task Ver_ficha_360_del_menu_navega_a_la_pagina_de_esa_fila()
    {
        var otro = Cliente("Aislamientos Nervión S.L.");
        var abierto = Cliente("Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Almacen = { otro, abierto } });

        await PulsarEnElMenuDeLaFila(cut, 1, "Abrir ficha 360");

        new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath
            .Should().Be($"/clientes/{abierto.Id}");
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse(
            "«Abrir ficha 360» lleva a la página; la vista previa es el nombre de la fila");
    }

    /// <summary>
    /// Pieza 5 del patrón: el «⋯» lleva el orden «Abrir ficha 360 · Editar ·
    /// Eliminar» (aquí «Dar de baja», la baja lógica) y ya no lleva «Vista
    /// rápida», que es el nombre de la fila.
    /// </summary>
    [Fact]
    public async Task El_menu_de_la_fila_sigue_el_orden_del_patron_de_lista()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Montajes Ebro S.L.") } });

        await cut.Find("tbody .menu-acciones .menu-acciones-disparador").ClickAsync(new MouseEventArgs());

        cut.FindAll("tbody .menu-acciones .menu-acciones-item").Select(i => i.TextContent.Trim())
            .Should().Equal("Abrir ficha 360", "Editar", "Dar de baja");
    }

    // ------------------------------------------------------ Teclado y lote

    /// <summary>
    /// Lo mismo que recorre el E2E P331 (j/k/x/Enter sin activar «Selección
    /// múltiple»), aquí sin navegador: j enfoca, x marca y la barra de lote
    /// dice «1 seleccionado en esta página», Enter abre la vista rápida (el
    /// panel de 520 px de la fila enfocada).
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
        cut.WaitForAssertion(() =>
            cut.Find("aside.drawer-preview-cliente .nombre-cabecera-preview-cliente").TextContent.Trim()
                .Should().Be("Montajes Ebro S.L."));
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

        await AlternarSeleccionMultiple(cut);
        cut.FindAll(".aviso-seleccion-pagina").Should().BeEmpty("aún no hay nada marcado");

        await cut.Find("thead input[type=checkbox]").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.Find(".aviso-seleccion-pagina").TextContent.Trim().Should().Be(
            "Los 20 de esta página están seleccionados. Hay 25 en total: los de otras páginas no entran en la selección.");
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("20 seleccionados en esta página");
        cut.Markup.Should().NotContain("filtrados");
    }

    /// <summary>Listados 5/7 (decisión D6, 2026-10-08): la baja en lote deja un único «Deshacer» que restaura los que cayeron.</summary>
    [Fact]
    public async Task La_baja_en_lote_ofrece_un_unico_Deshacer_que_restaura_los_que_cayeron()
    {
        var a = Cliente("Aislamientos Nervión S.L.");
        var b = Cliente("Montajes Ebro S.L.");
        var c = Cliente("Refrielectric S.A.");
        var mediador = new MediatorFalso { LoteDevuelveIds = true, Almacen = { a, b, c } };
        var cut = Renderizar(mediador);

        await AlternarSeleccionMultiple(cut);
        await cut.FindAll("tbody input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("tbody input[type=checkbox]")[2].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(x => x.TextContent.Trim() == "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        var avisos = Services.GetRequiredService<ToastService>();
        var aviso = avisos.Mensajes.Single(m => m.TextoAccion == "Deshacer");
        mediador.Enviadas.OfType<RestaurarClienteCommand>().Should().BeEmpty("ofrecer «Deshacer» no restaura nada");

        await cut.InvokeAsync(() => avisos.EjecutarAccionAsync(aviso.Id));

        mediador.Enviadas.OfType<RestaurarClienteCommand>().Select(r => r.Id).Should().BeEquivalentTo([a.Id, c.Id]);
        avisos.Mensajes.Should().Contain(m => m.Mensaje == "2 Cliente(s) empresarial(es) restaurado(s).");
    }

    [Fact]
    public async Task Eliminar_en_lote_pide_confirmacion_y_manda_solo_los_marcados()
    {
        var a = Cliente("Aislamientos Nervión S.L.");
        var b = Cliente("Montajes Ebro S.L.");
        var c = Cliente("Refrielectric S.A.");
        var mediador = new MediatorFalso { Almacen = { a, b, c } };
        var cut = Renderizar(mediador);

        await AlternarSeleccionMultiple(cut);
        await cut.FindAll("tbody input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("tbody input[type=checkbox]")[2].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(x => x.TextContent.Trim() == "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClientesCommand>().Should().BeEmpty("abrir el diálogo no borra nada");
        cut.Find("[role=dialog] h2").TextContent.Should().Be("¿Dar de baja 2 Cliente(s) empresarial(es)?");

        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClientesCommand>().Single().Ids.Should().BeEquivalentTo([a.Id, c.Id]);
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Montajes Ebro S.L."]));
    }

    // ---------------------------------------------------- Filtros guardados

    /// <summary>
    /// Los cuatro ejes puestos —búsqueda y críticos por la URL, Gestor CAE y
    /// estado por sus desplegables— y los cuatro en el JSON enviado. Un JSON al
    /// que le faltara cualquiera devolvería, al aplicarlo, una lista sin ese
    /// filtro.
    /// </summary>
    [Fact]
    public async Task Guardar_filtro_guarda_los_cuatro_filtros_y_no_solo_busqueda_y_criticos()
    {
        var marta = GestorCae("Marta Ibarra");
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", critico: true, peor: EstadoDocumento.Vencido, cantidad: 2) with { EjecutivoUsuarioId = marta.Id } }
        };
        var cut = Renderizar(mediador, "clientes?q=Refri&critico=true", gestores: [marta]);
        await ElegirEnLaPastilla(cut, "Gestor CAE", "Marta Ibarra");
        await AlternarEnLaFranja(cut, "Vencidos");

        // Punto de partida: los cuatro ejes están puestos en la consulta vigente.
        var vigente = UltimaConsulta(mediador);
        vigente.Busqueda.Should().Be("Refri");
        vigente.SoloCriticos.Should().BeTrue();
        vigente.EjecutivoUsuarioId.Should().Be(marta.Id);
        vigente.EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);

        await PulsarEnMasFiltros(cut, "Guardar filtro");
        await cut.Find("[role=dialog] input").InputAsync(new ChangeEventArgs { Value = "Refri críticos de Marta con vencidos" });
        await BotonDelDialogo(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var guardado = mediador.Enviadas.OfType<GuardarFiltroCommand>().Single();
        guardado.Nombre.Should().Be("Refri críticos de Marta con vencidos");
        using var json = JsonDocument.Parse(guardado.ValoresJson);
        json.RootElement.GetProperty("Busqueda").GetString().Should().Be("Refri");
        json.RootElement.GetProperty("SoloCriticos").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("GestorCaeId").GetString().Should().Be(marta.Id.ToString());
        json.RootElement.GetProperty("EstadoDocumental").GetString().Should().Be(nameof(EstadoDocumento.Vencido));
        cut.WaitForAssertion(() => cut.FindAll("[role=dialog]").Should().BeEmpty());
    }

    /// <summary>
    /// Aplicar un filtro guardado repone los cuatro filtros y escribe en la URL
    /// los dos que viajan por ella. Si solo los cambiara en memoria, la URL
    /// seguiría sin <c>q</c> ni <c>critico</c> y la siguiente pasada de
    /// parámetros los quitaría. El Gestor CAE no es nulo: Refri Levante cumple
    /// todo menos el Gestor CAE, y solo desaparece si el filtro lo repone.
    /// </summary>
    [Fact]
    public async Task Aplicar_un_filtro_guardado_repone_sus_filtros_en_la_consulta_y_en_la_url()
    {
        var marta = GestorCae("Marta Ibarra");
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Refri críticos de Marta con vencidos",
            JsonSerializer.Serialize(new { Busqueda = "Refri", SoloCriticos = true, GestorCaeId = marta.Id.ToString(), EstadoDocumental = "Vencido" }),
            DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Refrielectric S.A.", critico: true, peor: EstadoDocumento.Vencido, cantidad: 3) with { EjecutivoUsuarioId = marta.Id },
                Cliente("Refri Levante S.L.", critico: true, peor: EstadoDocumento.Vencido, cantidad: 1),
                Cliente("Refrigeración Norte S.L.", critico: true) with { EjecutivoUsuarioId = marta.Id },
                Cliente("Montajes Ebro S.L."),
            },
            FiltrosGuardados = { filtro }
        };
        var cut = Renderizar(mediador, gestores: [marta]);

        await PulsarEnMasFiltros(cut, filtro.Nombre);

        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().Be("Refri");
        consulta.SoloCriticos.Should().BeTrue();
        consulta.EjecutivoUsuarioId.Should().Be(marta.Id);
        consulta.EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);
        var uri = Services.GetRequiredService<NavigationManager>().Uri;
        uri.Should().Contain("q=Refri").And.Contain("critico=true").And.Contain("estado=Vencido");
        cut.MarcadosEnFranja().Should().Equal("Vencidos");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
        Pastilla(cut, "Gestor CAE").GetAttribute("aria-label").Should().Be("Gestor CAE: Marta Ibarra",
            "la pastilla enseña elegido al Gestor CAE del filtro");
        TextosDeLosChips(cut).Should().Contain(t => t.StartsWith("Gestor CAE: Marta Ibarra"));
    }

    /// <summary>
    /// Un filtro guardado antes de que existieran Ejecutivo y Estado solo
    /// declara búsqueda y críticos. Aplica esos dos —también el
    /// <c>SoloCriticos: false</c>, que quita el «solo críticos» de la URL— y NO
    /// toca Ejecutivo ni Estado: no dice nada de ellos.
    /// </summary>
    [Fact]
    public async Task Un_filtro_guardado_con_el_formato_antiguo_aplica_sus_dos_ejes_y_conserva_ejecutivo_y_estado()
    {
        var marta = GestorCae("Marta Ibarra");
        var antiguo = new FiltroGuardadoDto(Guid.NewGuid(), "Solo Refri", "{\"Busqueda\":\"Refri\",\"SoloCriticos\":false}", DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 2) with { EjecutivoUsuarioId = marta.Id },
                Cliente("Refrigeración Norte S.L.", peor: EstadoDocumento.Vencido, cantidad: 1),
                Cliente("Montajes Ebro S.L.", peor: EstadoDocumento.Vencido, cantidad: 4) with { EjecutivoUsuarioId = marta.Id },
            },
            FiltrosGuardados = { antiguo }
        };
        var cut = Renderizar(mediador, "clientes?critico=true", gestores: [marta]);
        await ElegirEnLaPastilla(cut, "Gestor CAE", "Marta Ibarra");
        await AlternarEnLaFranja(cut, "Vencidos");

        await PulsarEnMasFiltros(cut, antiguo.Nombre);

        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().Be("Refri");
        consulta.SoloCriticos.Should().BeNull("el filtro antiguo SÍ declara SoloCriticos: false");
        consulta.EjecutivoUsuarioId.Should().Be(marta.Id, "el filtro antiguo no declara Gestor CAE: se queda el que había");
        consulta.EstadosDocumentales.Should().Equal([EstadoDocumento.Vencido], "el filtro antiguo no declara estado: se queda el que había");
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("q=Refri").And.NotContain("critico");
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A."]));
        Pastilla(cut, "Gestor CAE").GetAttribute("aria-label").Should().Be("Gestor CAE: Marta Ibarra");
        TextosDeLosChips(cut).Should().Contain(t => t.StartsWith("Gestor CAE: Marta Ibarra"));
        cut.MarcadosEnFranja().Should().Equal(["Vencidos"], "el estado que había sigue marcado en la franja");
    }

    /// <summary>
    /// Un filtro nuevo declara los cuatro ejes aunque alguno vaya a null, y
    /// null es un valor: «sin Gestor CAE» y «sin estado» limpian lo que hubiera.
    /// </summary>
    [Fact]
    public async Task Un_filtro_guardado_nuevo_con_GestorCaeId_null_limpia_el_ejecutivo_y_el_estado()
    {
        var marta = GestorCae("Marta Ibarra");
        var nuevo = new FiltroGuardadoDto(Guid.NewGuid(), "Toda la cartera",
            "{\"Busqueda\":null,\"SoloCriticos\":false,\"GestorCaeId\":null,\"EstadoDocumental\":null}", DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                Cliente("Refrielectric S.A.", peor: EstadoDocumento.Vencido, cantidad: 2) with { EjecutivoUsuarioId = marta.Id },
                Cliente("Montajes Ebro S.L."),
            },
            FiltrosGuardados = { nuevo }
        };
        var cut = Renderizar(mediador, gestores: [marta]);
        await ElegirEnLaPastilla(cut, "Gestor CAE", "Marta Ibarra");
        await AlternarEnLaFranja(cut, "Vencidos");
        UltimaConsulta(mediador).EjecutivoUsuarioId.Should().Be(marta.Id, "punto de partida: hay Gestor CAE elegido");

        await PulsarEnMasFiltros(cut, nuevo.Nombre);

        var consulta = UltimaConsulta(mediador);
        consulta.EjecutivoUsuarioId.Should().BeNull("el filtro declara GestorCaeId: null");
        consulta.EstadosDocumentales.Should().BeNull("el filtro declara EstadoDocumental: null");
        consulta.EstadoDocumental.Should().BeNull();
        cut.MarcadosEnFranja().Should().Equal("Todos");
        Pastilla(cut, "Gestor CAE").GetAttribute("aria-label").Should().Be("Gestor CAE");
        // Peor estado primero: Refrielectric tiene vencidos.
        cut.WaitForAssertion(() => NombresDeLasFilas(cut).Should().Equal(["Refrielectric S.A.", "Montajes Ebro S.L."]));
        TextosDeLosChips(cut).Should().BeEmpty();
    }

    /// <summary>
    /// I14 del contrato del selector: los filtros guardados son del usuario y no del Tenant, así que uno
    /// guardado con la empresa anterior puede llevar el Id de un Gestor CAE que esta empresa no ve.
    /// No se repone (filtraría por alguien que la pantalla no puede nombrar): la consulta sale sin Gestor
    /// CAE, el desplegable queda vacío y no hay chip. Lo demás del filtro sí se aplica.
    /// </summary>
    [Fact]
    public async Task Un_filtro_guardado_con_un_Gestor_CAE_de_otra_empresa_no_lo_repone()
    {
        var marta = GestorCae("Marta Ibarra");
        var deOtraEmpresa = Guid.NewGuid();
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Guardado con la empresa anterior",
            JsonSerializer.Serialize(new { Busqueda = "Refri", SoloCriticos = false, GestorCaeId = deOtraEmpresa.ToString(), EstadoDocumental = (string?)null }),
            DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.") with { EjecutivoUsuarioId = marta.Id } },
            FiltrosGuardados = { filtro }
        };
        var cut = Renderizar(mediador, gestores: [marta]);

        await PulsarEnMasFiltros(cut, filtro.Nombre);

        var consulta = UltimaConsulta(mediador);
        consulta.Busqueda.Should().Be("Refri", "el resto del filtro sí se aplica");
        consulta.EjecutivoUsuarioId.Should().BeNull("el Gestor CAE del filtro no existe en esta empresa");
        Pastilla(cut, "Gestor CAE").GetAttribute("aria-label").Should().Be("Gestor CAE");
        TextosDeLosChips(cut).Should().NotContain(t => t.StartsWith("Gestor CAE"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain(deOtraEmpresa.ToString());
    }

    /// <summary>
    /// <c>ValoresJson</c> vive en la tabla <c>FiltrosGuardados</c> y Application
    /// solo exige que no esté vacío: puede llegar corrupto, con otra forma o
    /// con un tipo que no es el suyo. Ninguno tumba el circuito: los filtros se
    /// quedan como estaban, no se recarga nada y se avisa. Después la página
    /// sigue respondiendo.
    /// </summary>
    [Theory]
    [InlineData("{no es json")]
    [InlineData("[\"Refri\"]")]
    [InlineData("{\"Busqueda\":\"Refri\",\"SoloCriticos\":\"sí\"}")]
    public async Task Un_filtro_guardado_ilegible_avisa_y_deja_los_filtros_como_estaban(string valoresJson)
    {
        var roto = new FiltroGuardadoDto(Guid.NewGuid(), "Roto", valoresJson, DateTime.UtcNow);
        var mediador = new MediatorFalso
        {
            Almacen = { Cliente("Refrielectric S.A.", critico: true, peor: EstadoDocumento.Vencido, cantidad: 1), Cliente("Montajes Ebro S.L.") },
            FiltrosGuardados = { roto }
        };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();
        await AlternarEnLaFranja(cut, "Vencidos");
        var consultasAntes = mediador.Enviadas.OfType<ObtenerClientesQuery>().Count();
        var uriAntes = navegacion.Uri;

        await PulsarEnMasFiltros(cut, roto.Nombre);

        mediador.Enviadas.OfType<ObtenerClientesQuery>().Should().HaveCount(consultasAntes, "no se aplicó nada, así que no hay nada que recargar");
        UltimaConsulta(mediador).EstadosDocumentales.Should().Equal(EstadoDocumento.Vencido);
        navegacion.Uri.Should().Be(uriAntes);
        cut.MarcadosEnFranja().Should().Equal(["Vencidos"], "el estado que había sigue marcado: no se tocó ningún filtro");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m =>
            m.Tono == TonoToast.Advertencia && m.Mensaje.StartsWith("No se pudo aplicar este filtro guardado"));

        await ElegirEnLaPastilla(cut, "Criticidad", "Solo críticos");
        UltimaConsulta(mediador).SoloCriticos.Should().BeTrue("la página sigue viva y aplica el filtro siguiente");
    }

    [Fact]
    public async Task Borrar_un_filtro_guardado_pide_confirmacion_con_su_efecto_y_solo_entonces_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Cartera Levante", "{\"Busqueda\":null,\"SoloCriticos\":true}", DateTime.UtcNow);
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") }, FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await PulsarBorrarFiltroGuardado(cut, "Cartera Levante");

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty("pulsar el aspa solo pide confirmación");
        var dialogo = cut.Find("[role=dialog]");
        dialogo.QuerySelector("h2")!.TextContent.Should().Be("¿Borrar el filtro guardado «Cartera Levante»?");
        dialogo.TextContent.Should().Contain("No borra ningún Cliente empresarial");

        await BotonDelDialogo(cut, "Borrar filtro").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().Equal([new EliminarFiltroGuardadoCommand(filtro.Id)]);
        cut.FindAll("[role=dialog]").Should().BeEmpty();
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado").Should().BeEmpty());
    }

    [Fact]
    public async Task Cancelar_el_borrado_de_un_filtro_guardado_no_lo_borra()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Cartera Levante", "{\"Busqueda\":null,\"SoloCriticos\":true}", DateTime.UtcNow);
        var mediador = new MediatorFalso { FiltrosGuardados = { filtro } };
        var cut = Renderizar(mediador);

        await PulsarBorrarFiltroGuardado(cut, "Cartera Levante");
        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarFiltroGuardadoCommand>().Should().BeEmpty();
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        cut.FindAll(".barra-filtros-pastillas .menu-filtro-guardado").Should().ContainSingle(c => c.TextContent.Contains("Cartera Levante"));
    }

    /// <summary>
    /// Los filtros guardados viven dentro de «Más filtros» (rediseño de listados, fase 1): uno
    /// por línea con su ✕, y «Guardar filtro» al final, deshabilitado mientras no haya filtros
    /// que guardar.
    /// </summary>
    [Fact]
    public async Task Mas_filtros_lista_los_guardados_con_su_aspa_y_Guardar_filtro_sin_filtros_no_se_puede_pulsar()
    {
        var filtro = new FiltroGuardadoDto(Guid.NewGuid(), "Cartera Levante", "{\"Busqueda\":null,\"SoloCriticos\":true}", DateTime.UtcNow);
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") }, FiltrosGuardados = { filtro } });

        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());

        var guardado = cut.Find(".barra-filtros-pastillas .menu-filtro-guardado");
        guardado.QuerySelectorAll("[role=menuitem]").Select(i => i.GetAttribute("aria-label") ?? i.TextContent.Trim())
            .Should().Equal("Cartera Levante", "Borrar filtro guardado Cartera Levante");
        var guardar = cut.FindAll(".barra-filtros-pastillas [role=menuitem]").Single(i => i.TextContent.Trim() == "Guardar filtro");
        guardar.HasAttribute("disabled").Should().BeTrue("sin filtros aplicados no hay nada que guardar");
        cut.FindAll(".chip-filtro").Should().BeEmpty("los filtros guardados ya no se pintan como chips");
    }

    private static async Task PulsarBorrarFiltroGuardado(IRenderedComponent<Clientes> cut, string nombre)
    {
        await Pastilla(cut, "Más filtros").ClickAsync(new MouseEventArgs());
        await cut.Find($".barra-filtros-pastillas [aria-label='Borrar filtro guardado {nombre}']").ClickAsync(new MouseEventArgs());
    }

    // ------------------------------------------- El propio doble de la consulta

    /// <summary>
    /// Con filtro de estado, el handler solo calcula el agregado de los 2.000
    /// primeros candidatos en orden: un cliente con vencidos más allá no cuenta.
    /// Cliente 0001 es el control positivo (dentro del tope, sí cuenta).
    /// </summary>
    [Fact]
    public void El_doble_con_filtro_de_estado_solo_mira_los_primeros_2000_candidatos_como_el_handler()
    {
        var mediador = new MediatorFalso();
        mediador.Almacen.Add(Cliente("Cliente 0001", peor: EstadoDocumento.Vencido, cantidad: 1));
        for (var i = 2; i <= MediatorFalso.LimiteCandidatosConFiltroDeEstado; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:0000}"));
        mediador.Almacen.Add(Cliente("Cliente 9999", peor: EstadoDocumento.Vencido, cantidad: 1));

        var conEstado = mediador.Filtrar(new ObtenerClientesQuery(null, null, EstadoDocumental: EstadoDocumento.Vencido));

        conEstado.TotalElementos.Should().Be(1, "Cliente 9999 es el candidato 2.001 y queda fuera del tope");
        conEstado.Elementos.Select(c => c.RazonSocial).Should().Equal(["Cliente 0001"]);
        mediador.Filtrar(new ObtenerClientesQuery(null, null)).TotalElementos
            .Should().Be(MediatorFalso.LimiteCandidatosConFiltroDeEstado + 1, "sin filtro de estado no hay tope");
    }

    /// <summary>
    /// Con el orden por Estado documental (el de por defecto) y sin filtro de estado, el handler
    /// ordena la cartera entera (ObtenerClientesOrdenPorEstadoDocumentalTests lo fija contra
    /// PostgreSQL): el total es el real y un Vencido cuyo nombre va el último sale el primero.
    /// </summary>
    [Fact]
    public void El_doble_con_orden_por_estado_ordena_la_cartera_entera_como_el_handler()
    {
        var mediador = new MediatorFalso();
        for (var i = 1; i <= MediatorFalso.LimiteCandidatosConFiltroDeEstado; i++)
            mediador.Almacen.Add(Cliente($"Cliente {i:0000}"));
        mediador.Almacen.Add(Cliente("Cliente 9999", peor: EstadoDocumento.Vencido, cantidad: 1));
        mediador.Almacen.Add(Cliente("Aaa Primero", peor: EstadoDocumento.Faltante, cantidad: 1));

        var ordenados = mediador.Filtrar(new ObtenerClientesQuery(null, null, OrdenarPor: nameof(ClienteListaDto.EstadoDocumentalPeor)));

        ordenados.TotalElementos.Should().Be(MediatorFalso.LimiteCandidatosConFiltroDeEstado + 2);
        ordenados.Elementos.Select(c => c.RazonSocial).Should().StartWith(["Cliente 9999", "Aaa Primero", "Cliente 0001"],
            "el Vencido va primero aunque por nombre sea el candidato 2.002; después el Faltante (Vencido pesa más desde el 2026-10-03) y el resto por razón social");
    }

    /// <summary>
    /// Empatados en el orden elegido, el handler cierra con el Id. Se insertan
    /// al revés para que el orden de inserción no dé la respuesta por casualidad.
    /// </summary>
    [Fact]
    public void El_doble_desempata_por_Id_como_el_handler()
    {
        var primeroPorId = Cliente("Refrielectric S.A.") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000001") };
        var segundoPorId = Cliente("Refrielectric S.A.") with { Id = Guid.Parse("00000000-0000-0000-0000-000000000002") };
        var mediador = new MediatorFalso { Almacen = { segundoPorId, primeroPorId } };

        mediador.Filtrar(new ObtenerClientesQuery(null, null)).Elementos.Select(c => c.Id)
            .Should().Equal([primeroPorId.Id, segundoPorId.Id]);
    }

    /// <summary>
    /// El Workspace no es modal: con la ficha del Cliente empresarial abierta, la
    /// baja se confirma desde la fila que queda detrás. La ficha ya no tiene baja
    /// propia (P41b), así que la lista es quien la retira.
    /// </summary>
    [Fact]
    public async Task Eliminar_el_cliente_cuya_ficha_esta_abierta_retira_la_ficha()
    {
        var a = Cliente("Refrielectric S.A.");
        var mediador = new MediatorFalso { Almacen = { a } };
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Cliente, a.Id, a.RazonSocial, "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await cut.Find("tbody .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        await cut.FindAll("tbody .menu-acciones-item").Single(b => b.TextContent.Trim() == "Dar de baja").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClienteCommand>().Should().ContainSingle("la baja se ejecutó");
        workspace.EstaAbierto.Should().BeFalse("una ficha abierta de un cliente ya dado de baja no puede seguir editable");
    }

    /// <summary>
    /// Baja en lote. El DTO del lote no dice qué ids cayeron, así que la lista
    /// retira las fichas de TODO lo pedido en cuanto cayó alguno (decisión: pasarse
    /// de retirar antes que dejar abierta una ficha muerta). Y no toca la ficha de
    /// un cliente que no iba en el lote.
    /// </summary>
    [Theory]
    [InlineData(true, false)]  // iba en el lote, lote completo
    [InlineData(true, true)]   // iba en el lote, lote parcial: se retira igualmente
    [InlineData(false, false)] // no iba en el lote: se queda
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_de_lo_pedido_y_solo_de_lo_pedido(bool ibaEnElLote, bool parcial)
    {
        var pedido = Cliente("Aislamientos Nervión S.L.");
        var otro = Cliente("Refrielectric S.A.");
        var mediador = new MediatorFalso { Almacen = { pedido, otro } };
        if (parcial) mediador.ErroresDeLote.Add("Un cliente con centros activos no puede eliminarse.");
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var abierto = ibaEnElLote ? pedido : otro;
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Cliente, abierto.Id, abierto.RazonSocial, "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await AlternarSeleccionMultiple(cut);
        await cut.FindAll("tbody input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(x => x.TextContent.Trim() == "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClientesCommand>().Single().Ids.Should().Equal([pedido.Id],
            "el caso solo vale si el lote pidió a ese cliente y a nadie más");
        workspace.EstaAbierto.Should().Be(!ibaEnElLote);
    }

    /// <summary>La guarda de la retirada: un lote que no eliminó NADA no toca la ficha abierta de un cliente que iba en él.</summary>
    [Fact]
    public async Task Un_lote_que_no_elimina_nada_no_retira_la_ficha_abierta()
    {
        var pedido = Cliente("Aislamientos Nervión S.L.");
        var mediador = new MediatorFalso { Almacen = { pedido, Cliente("Refrielectric S.A.") }, EliminadosForzados = 0 };
        mediador.ErroresDeLote.Add("Un cliente con centros activos no puede eliminarse.");
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Cliente, pedido.Id, pedido.RazonSocial, "informacion"));

        await AlternarSeleccionMultiple(cut);
        await cut.FindAll("tbody input[type=checkbox]")[0].ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(x => x.TextContent.Trim() == "Dar de baja seleccionados").ClickAsync(new MouseEventArgs());
        await BotonDelDialogo(cut, "Dar de baja").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarClientesCommand>().Single().Ids.Should().Equal([pedido.Id], "el caso solo vale si el lote pidió a ese cliente");
        workspace.EstaAbierto.Should().BeTrue("no cayó nada: no hay nada muerto que retirar");
    }

    // --- Empresa gestionada activa (lote 3 del selector de Tenant beneficiario) ----------------

    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaNorte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaSur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static MediatorFalso ConCartera(bool origenGestionado)
    {
        var mediador = new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } };
        mediador.Autorizados.Clear();
        mediador.Autorizados.AddRange(
        [
            new ClienteAutorizadoDto(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: origenGestionado),
            new ClienteAutorizadoDto(EmpresaNorte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(EmpresaSur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
        ]);
        return mediador;
    }

    /// <summary>
    /// Rediseño de listados, fase 1: la cabecera de una línea ya no repite qué empresa
    /// gestionada está activa —lo dice el selector de la barra lateral—, pero la lista sí es la
    /// de esa empresa.
    /// </summary>
    [Fact]
    public void Con_varias_empresas_gestionadas_la_lista_es_la_de_la_activa_sin_rotulo_en_la_cabecera()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);

        var cut = Renderizar(ConCartera(origenGestionado: false));

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Find("header.cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("1");
        cut.Markup.Should().Contain("Refrielectric S.A.");
    }

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_cabecera_de_empresa_gestionada()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { Cliente("Refrielectric S.A.") } });

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().Contain("Refrielectric S.A.");
    }

    [Fact]
    public void Sin_empresa_elegida_y_con_el_origen_sin_gestionar_pide_elegir_y_no_muestra_datos_del_origen()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador);

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.Markup.Should().NotContain("Refrielectric S.A.");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty("no hay empresa activa que nombrar en el estado 4a");
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("su «Exportar a Excel» exportaría los datos del origen");
        cut.FindAll("header.cabecera-pagina .cabecera-listado-contador").Should().BeEmpty();
        ConsultasDeLista(mediador).Should().Be(0, "no se piden los clientes de la organización de origen");
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().BeEmpty("tampoco los filtros guardados del origen");
    }

    /// <summary>Un enlace con filtros (<c>?q=</c>) no salta el estado 4a: los parámetros de la URL no piden la lista del origen.</summary>
    [Fact]
    public void Sin_empresa_elegida_un_enlace_con_filtros_tampoco_pide_la_lista_del_origen()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador, "clientes?q=Refri&critico=true");

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.Markup.Should().NotContain("Refrielectric S.A.");
        ConsultasDeLista(mediador).Should().Be(0, "la URL trae filtros pero no hay empresa elegida");
    }

    /// <summary>Tampoco lo salta la acción por URL (atajo «n», palette): el alta se abriría contra la organización de origen.</summary>
    [Fact]
    public void Sin_empresa_elegida_la_accion_crear_de_la_url_no_abre_el_alta()
    {
        var cut = Renderizar(ConCartera(origenGestionado: false), "clientes?accion=crear");

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.FindAll("[role=dialog]").Should().BeEmpty("el alta iría al Tenant de origen, que no es la empresa activa");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var cut = Renderizar(ConCartera(origenGestionado: true));

        cut.Markup.Should().NotContain("Selecciona una empresa de tu cartera");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().Contain("Refrielectric S.A.");
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista_ni_se_ofrece_la_exportacion()
    {
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes");
        var cut = Render<Clientes>();
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("el «⋯» lleva «Exportar a Excel»");
        ConsultasDeLista(mediador).Should().Be(0);

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Selecciona una empresa de tu cartera"));
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty();
        ConsultasDeLista(mediador).Should().Be(0, "ni antes ni después de resolverse se pide la lista del origen");
    }

    /// <summary>Salir de la página con la resolución en vuelo la cancela: no se repinta ni se pide la lista de nadie.</summary>
    [Fact]
    public void Salir_de_la_pagina_con_la_empresa_activa_en_vuelo_cancela_la_resolucion()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes");
        var cut = Render<Clientes>();
        mediador.TokenDeAutorizados.Should().NotBeNull("la página pidió la lista de Tenants autorizados");
        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeFalse("control positivo: sigue montada");

        cut.Instance.Dispose();

        mediador.TokenDeAutorizados!.Value.IsCancellationRequested.Should().BeTrue();
        puerta.SetResult();
        ConsultasDeLista(mediador).Should().Be(0);
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().BeEmpty();
    }

    /// <summary>
    /// Si la resolución vuelve sin lanzar tras retirarse la página (contexto sin empresa activa ni 4a), la
    /// página tampoco sigue: ni lista ni filtros guardados de una página que ya no existe.
    /// </summary>
    [Fact]
    public void Una_resolucion_que_vuelve_sin_lanzar_tras_retirar_la_pagina_no_pide_ni_la_lista_ni_los_filtros_guardados()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        mediador.IgnorarCancelacionDeAutorizados = true;
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes");
        var cut = Render<Clientes>();
        cut.Instance.Dispose();

        puerta.SetResult();

        ConsultasDeLista(mediador).Should().Be(0);
        mediador.Enviadas.OfType<ObtenerFiltrosGuardadosQuery>().Should().BeEmpty();
        mediador.Enviadas.Should().OnlyContain(e => e is ObtenerClientesAutorizadosQuery, "tras retirarse solo consta la resolución que ya iba en vuelo");
    }

    /// <summary>
    /// Codex, pasada 1: tras retirarse la página con la resolución en vuelo, ComponentBase todavía invoca
    /// <c>OnParametersSetAsync</c> con <c>_resolviendoEmpresa</c> ya en false. Con <c>?accion=crear</c> en la URL
    /// abriría el alta (y un intento de render) en un componente muerto.
    /// </summary>
    [Fact]
    public void Retirada_la_pagina_con_la_resolucion_en_vuelo_no_se_procesan_los_parametros_de_la_url()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes?accion=crear");
        var cut = Render<Clientes>();
        cut.Instance.Dispose();
        puerta.SetResult();

        // El componente está retirado y no hay DOM que mirar: se lee el estado que AbrirCrear habría fijado.
        var drawerVisible = (bool)typeof(Clientes)
            .GetField("_drawerVisible", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(cut.Instance)!;
        drawerVisible.Should().BeFalse("una página retirada no atiende ?accion=crear");
    }

    [Fact]
    public void Con_el_contexto_resuelto_a_una_empresa_la_lista_se_monta_tras_la_carga()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        Registrar(mediador);
        Services.GetRequiredService<NavigationManager>().NavigateTo("clientes");
        var cut = Render<Clientes>();
        ConsultasDeLista(mediador).Should().Be(0);
        cut.Markup.Should().NotContain("Refrielectric S.A.");

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Refrielectric S.A."));
    }
}
