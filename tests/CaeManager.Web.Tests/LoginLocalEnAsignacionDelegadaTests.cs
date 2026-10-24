using System.Collections;
using System.Linq.Expressions;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Soporte;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public partial class LoginLocalEnAsignacionDelegadaTests
{
    [Theory]
    [InlineData(Roles.GestorCae, Roles.GestorCae, Roles.Consulta)]
    [InlineData(Roles.CoordinadorCae, Roles.CoordinadorCae, Roles.Consulta)]
    [InlineData(Roles.Cliente, Roles.GestorCae, null)]
    [InlineData("", Roles.GestorCae, null)]
    [InlineData("Desconocido", Roles.GestorCae, null)]
    public async Task Login_local_con_asignacion_heredada_editora_no_supera_el_techo_de_la_sesion(
        string rolOrigen, string rolAsignado, string? esperado)
    {
        using var escenario = await Escenario.CrearAsync(rolOrigen, rolAsignado);

        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(esperado);
        await escenario.AplicarMiddlewareAsync();
        escenario.Contexto.User.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().Equal(esperado is null ? [] : [esperado]);
        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(esperado,
            "el circuito conserva el techo de origen después de sustituir los claims");
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Sin_restriccion_la_misma_asignacion_heredada_conserva_el_rol_editor(bool entraConfigurado, bool esSso)
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.CoordinadorCae, entraConfigurado, esSso);

        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(Roles.CoordinadorCae);
        await escenario.AplicarMiddlewareAsync();
        escenario.Contexto.User.IsInRole(Roles.CoordinadorCae).Should().BeTrue();
        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(Roles.CoordinadorCae);
    }

    [Theory]
    [InlineData(false, Roles.Consulta)]
    [InlineData(true, Roles.CoordinadorCae)]
    public async Task La_via_de_Operacion_conserva_el_rol_de_sesion_con_su_restriccion(bool esSso, string esperado)
    {
        using var escenario = await Escenario.CrearAsync(Roles.CoordinadorCae, Roles.GestorCae, esSso: esSso, porOperacion: true);

        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(esperado);
        await escenario.AplicarMiddlewareAsync();
        escenario.Contexto.User.IsInRole(esperado).Should().BeTrue();
        (await escenario.Servicio.OperaPorAsignacionDeOperacionAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_fan_out_resuelve_cada_Tenant_sin_heredar_el_rol_del_seleccionado(bool esSso)
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.CoordinadorCae, esSso: esSso);
        await escenario.AplicarMiddlewareAsync();

        using (AmbitoTenantExplicito.Establecer(escenario.TenantVisitado))
            (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(esSso ? Roles.CoordinadorCae : Roles.Consulta);
        using (AmbitoTenantExplicito.Establecer(Guid.NewGuid()))
            (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().BeNull("no existe asignación sobre este Tenant propietario");
        using (AmbitoTenantExplicito.Establecer(escenario.TenantOrigen))
            (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().Be(esSso ? Roles.GestorCae : Roles.Consulta);
    }

    [Theory]
    [InlineData("sin-asignacion")]
    [InlineData("otro-usuario")]
    [InlineData("revocada")]
    [InlineData("inactiva")]
    [InlineData("caducada")]
    public async Task La_restriccion_no_concede_rol_sin_asignacion_propia_vigente(string estado)
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.GestorCae);
        switch (estado)
        {
            case "sin-asignacion": escenario.Asignaciones.Clear(); break;
            case "otro-usuario":
                escenario.Asignaciones.Clear();
                escenario.Asignaciones.Add(new AsignacionOperadorDelegado(escenario.Delegacion.Id, Guid.NewGuid(), Roles.GestorCae));
                break;
            case "revocada": escenario.Asignaciones[0].Revocar("Retirada de prueba", DateTime.UtcNow); break;
            case "inactiva": escenario.Delegacion.Desactivar(); break;
            case "caducada":
                escenario.Delegacion.Desactivar();
                var ayer = DateTime.UtcNow.AddDays(-1);
                escenario.Delegacion.ActivarParaSoporte("Incidencia de prueba", ayer.AddHours(1), ayer);
                break;
        }

        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
        await escenario.AplicarMiddlewareAsync();
        escenario.Contexto.User.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Soporte_TALVEG_con_Sesion_Privilegiada_conserva_la_lectura_sin_rol_de_negocio(bool esSso)
    {
        using var escenario = await Escenario.CrearAsync(Roles.GestorCae, Roles.CoordinadorCae, esSso: esSso, privilegiada: true);
        var sesion = new SesionPrivilegiadaActiva(escenario.Seleccion.SesionPrivilegiadaIdSeleccionada!.Value,
            Guid.NewGuid(), escenario.TenantVisitado, CapacidadPrivilegio.SoporteLectura, null);
        await new SesionPrivilegiadaSinRolDeNegocioMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(escenario.Contexto, escenario.Protector);
        await escenario.AplicarMiddlewareAsync();

        (await escenario.Servicio.ObtenerUsuarioActualIdAsync()).Should().Be(escenario.Usuario);
        (await escenario.Servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
        escenario.Contexto.User.FindAll(ClaimTypes.Role).Should().BeEmpty();
        var tenantActual = new TenantActualAmbiental { TenantId = escenario.TenantVisitado };
        await using var db = new CaeManagerDbContext(new DbContextOptionsBuilder<CaeManagerDbContext>().Options,
            escenario.Protector, tenantActual);
        var alcance = new AlcanceDatosService(db, escenario.Servicio, tenantActual, new SesionVigente(sesion));
        (await alcance.TieneAccesoTotalAsync()).Should().BeTrue("la capacidad SoporteLectura sigue abriendo el Tenant objetivo");
        tenantActual.TenantId = Guid.NewGuid();
        (await alcance.TieneAccesoTotalAsync()).Should().BeFalse("el privilegio de plataforma no cruza al otro Tenant propietario");
    }

    private sealed class SesionVigente(SesionPrivilegiadaActiva sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) => Task.FromResult<SesionPrivilegiadaActiva?>(sesion);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) => ObtenerAsync(cancellationToken);
    }

    private sealed class Escenario : IDisposable, ITenantsQueryContext, IOperacionesQueryContext
    {
        public Guid Usuario { get; } = Guid.NewGuid();
        public Guid TenantOrigen { get; } = Guid.NewGuid();
        public Guid TenantVisitado { get; } = Guid.NewGuid();
        public IDataProtectionProvider Protector { get; } = DataProtectionProvider.Create(nameof(LoginLocalEnAsignacionDelegadaTests));
        public DefaultHttpContext Contexto { get; } = new();
        public DelegacionTenant Delegacion { get; private set; } = null!;
        public List<AsignacionOperadorDelegado> Asignaciones { get; } = [];
        public CurrentUserService Servicio { get; private set; } = null!;
        public ClienteActivoSeleccionado Seleccion { get; private set; } = null!;
        private ServiceProvider proveedor = null!;
        private AsignacionOperacion operacion = null!;

        public IQueryable<Tenant> Tenants => throw new NotSupportedException();
        public IQueryable<RegistroActividadSoporte> RegistrosActividadSoporte => throw new NotSupportedException();
        public IQueryable<DelegacionTenant> DelegacionesTenant => Consulta(new[] { Delegacion });
        public IQueryable<AsignacionOperadorDelegado> AsignacionesOperadorDelegado => Consulta(Asignaciones.Where(a => !a.EstaRevocada));
        public IQueryable<AsignacionOperacion> AsignacionesOperacion => Consulta(new[] { operacion });
        public IQueryable<AsignacionCartera> AsignacionesCartera => Consulta(Array.Empty<AsignacionCartera>());

        public static async Task<Escenario> CrearAsync(string rolOrigen, string rolAsignado, bool entraConfigurado = true,
            bool esSso = false, bool porOperacion = false, bool privilegiada = false)
        {
            var escenario = new Escenario();
            var ahora = DateTime.UtcNow;
            escenario.Delegacion = DelegacionTenant.ParaSoporte(escenario.TenantOrigen, escenario.TenantVisitado);
            escenario.Delegacion.ActivarParaSoporte("Incidencia ficticia", ahora.AddHours(1), ahora);
            escenario.Asignaciones.Add(new AsignacionOperadorDelegado(escenario.Delegacion.Id, escenario.Usuario, rolAsignado));
            escenario.operacion = AsignacionOperacion.Externa(escenario.TenantVisitado, escenario.TenantOrigen,
                ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddHours(-1), null, ahora);
            // La vía heredada no debe encontrar una operación alternativa en el fan-out.
            if (!porOperacion) escenario.operacion.Suspender();
            var identidad = new ClaimsIdentity("Prueba");
            identidad.AddClaim(new Claim(ClaimTypes.NameIdentifier, escenario.Usuario.ToString()));
            identidad.AddClaim(new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, escenario.TenantOrigen.ToString()));
            if (rolOrigen.Length > 0) identidad.AddClaim(new Claim(ClaimTypes.Role, rolOrigen));
            if (esSso) identidad.AddClaim(new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin,
                RestriccionLoginLocalClaimsTransformation.MetodoLoginSso));
            escenario.Contexto.User = await new RestriccionLoginLocalClaimsTransformation(Options.Create(new AzureAdOptions
            {
                TenantId = entraConfigurado ? "tenant-ficticio" : null,
                ClientId = entraConfigurado ? "aplicacion-ficticia" : null,
                ClientSecret = entraConfigurado ? "valor-ficticio" : null,
                TenantHydraId = entraConfigurado ? Guid.Parse("00000000-0000-0000-0000-0000000000aa") : null,
            })).TransformAsync(new ClaimsPrincipal(identidad));
            var token = ClienteActivoSeleccionado.Proteger(escenario.Protector, escenario.Usuario, escenario.TenantVisitado,
                asignacionOperacionId: porOperacion ? escenario.operacion.Id : null,
                sesionPrivilegiadaId: privilegiada ? Guid.NewGuid() : null);
            escenario.Contexto.Request.Headers.Cookie = $"{ClienteActivoSeleccionado.NombreCookie}={token}";
            var accesor = new AccesorFijo(escenario.Contexto);
            escenario.Seleccion = new ClienteActivoSeleccionado(accesor, escenario.Protector);
            escenario.proveedor = new ServiceCollection().AddSingleton<ITenantsQueryContext>(escenario)
                .AddSingleton<IOperacionesQueryContext>(escenario).BuildServiceProvider();
            escenario.Servicio = new CurrentUserService(new CircuitoFijo(escenario.Contexto.User), accesor,
                escenario.Seleccion, escenario.proveedor);
            return escenario;
        }

        public Task AplicarMiddlewareAsync() => new RolEfectivoDelWorkspaceMiddleware(_ => Task.CompletedTask)
            .InvokeAsync(Contexto, Seleccion, Servicio, Servicio, NullLogger<RolEfectivoDelWorkspaceMiddleware>.Instance);

        public void Dispose() => proveedor.Dispose();
    }

    private sealed class CircuitoFijo(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private sealed class AccesorFijo(HttpContext contexto) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get => contexto; set => throw new NotSupportedException(); }
    }

    private static IQueryable<T> Consulta<T>(IEnumerable<T> datos) => new ConsultaAsincrona<T>(datos.AsQueryable());

    private sealed class ConsultaAsincrona<T>(IQueryable<T> interna) : IOrderedQueryable<T>, IAsyncEnumerable<T>
    {
        public Type ElementType => typeof(T);
        public Expression Expression => interna.Expression;
        public IQueryProvider Provider => new ProveedorAsincrono(interna.Provider);
        public IEnumerator<T> GetEnumerator() => interna.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new EnumeradorAsincrono<T>(GetEnumerator());
    }

    private sealed class ProveedorAsincrono(IQueryProvider interno) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new ConsultaAsincrona<TElement>(interno.CreateQuery<TElement>(expression));
        public object? Execute(Expression expression) => interno.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => interno.Execute<TResult>(expression);
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            var tipoResultado = typeof(TResult).GetGenericArguments()[0];
            var resultado = interno.Execute(expression);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(tipoResultado).Invoke(null, [resultado])!;
        }
    }

    private sealed class EnumeradorAsincrono<T>(IEnumerator<T> interno) : IAsyncEnumerator<T>
    {
        public T Current => interno.Current;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(interno.MoveNext());
        public ValueTask DisposeAsync() { interno.Dispose(); return ValueTask.CompletedTask; }
    }
}