using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>Mismo fallo real que TenantActualTests, pero para el rol/Id de usuario que consume IAlcanceDatosService.</summary>
public class CurrentUserServiceTests
{
    private static readonly Guid UsuarioIdDeEjemplo = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Resuelve_rol_e_id_desde_el_circuito_de_blazor_cuando_hay_uno()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador"));
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var servicio = CrearServicio(authStateProvider, httpContextAccessor);

        (await servicio.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo);
        (await servicio.ObtenerRolActualAsync()).Should().Be("Administrador");
    }

    [Fact]
    public async Task Cae_a_HttpContext_cuando_no_hay_circuito_de_blazor_pero_si_usuario_autenticado_por_cookie()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador"));
        var servicio = CrearServicio(authStateProvider, httpContextAccessor);

        (await servicio.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo);
        (await servicio.ObtenerRolActualAsync()).Should().Be("Administrador");
    }

    [Fact]
    public async Task Devuelve_null_si_no_hay_circuito_ni_HttpContext_autenticado()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var servicio = CrearServicio(authStateProvider, httpContextAccessor);

        (await servicio.ObtenerUsuarioActualIdAsync()).Should().BeNull();
        (await servicio.ObtenerRolActualAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Bajo_una_sesion_privilegiada_no_hay_rol_de_negocio_aunque_el_claim_diga_Administrador()
    {
        // El peor caso real: un tecnico de TALVEG que es Administrador en SU
        // tenant abre el de un cliente por el plano 3. Devolver el claim le
        // daria dentro del tenant visitado el rol que tiene en el suyo — el
        // hallazgo N-5 en su version mas grave. Una sesion privilegiada no es
        // miembro del workspace que visita: ni rol CAE ni cartera
        // (ADR-011 § 4bis.3).
        var authStateProvider = new AuthenticationStateProviderFalso(
            UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador"));

        var servicio = new CurrentUserService(
            authStateProvider, new HttpContextAccessorFalso(null),
            new ClienteActivoSeleccionadoConSesionPrivilegiadaFalso(),
            new ServiceCollection().BuildServiceProvider());

        // El usuario sigue siendo el mismo — la auditoria necesita saber quien
        // es. Lo que desaparece es el rol.
        (await servicio.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo);
        (await servicio.ObtenerRolActualAsync()).Should().BeNull();
    }

    /// <summary>
    /// Sin Delegated Workspace seleccionado, que es el caso de todo usuario
    /// que no es Operador Delegado: <c>ObtenerRolActualAsync</c> devuelve el
    /// claim sin resolver nada del contenedor ni tocar la base de datos, por
    /// eso basta un proveedor vacío (ver CurrentUserService). El camino
    /// delegado se cubre en CaeManager.IntegrationTests, con contexto real.
    /// </summary>
    private static CurrentUserService CrearServicio(
        AuthenticationStateProvider authStateProvider, IHttpContextAccessor httpContextAccessor) =>
        new(authStateProvider, httpContextAccessor, new ClienteActivoSeleccionadoFalso(),
            new ServiceCollection().BuildServiceProvider());

    private sealed class ClienteActivoSeleccionadoFalso : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    /// <summary>
    /// Selección de plano 3: el token nombra una sesión privilegiada. Que
    /// aquí no se revalide es deliberado — negar de más siempre es seguro, y
    /// quien decide si la sesión vale es ISesionPrivilegiadaActual.
    /// </summary>
    private sealed class ClienteActivoSeleccionadoConSesionPrivilegiadaFalso : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => Guid.Parse("33333333-3333-3333-3333-333333333333");
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => Guid.Parse("44444444-4444-4444-4444-444444444444");
    }

    private static ClaimsPrincipal UsuarioAutenticadoCon(Guid usuarioId, string rol)
    {
        var identidad = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString()), new Claim(ClaimTypes.Role, rol)], "prueba");
        return new ClaimsPrincipal(identidad);
    }

    private sealed class AuthenticationStateProviderFalso : AuthenticationStateProvider
    {
        private readonly ClaimsPrincipal? _usuario;
        private readonly bool _lanzarInvalidOperationException;

        public AuthenticationStateProviderFalso(ClaimsPrincipal? usuario = null, bool lanzarInvalidOperationException = false)
        {
            _usuario = usuario;
            _lanzarInvalidOperationException = lanzarInvalidOperationException;
        }

        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            if (_lanzarInvalidOperationException)
                throw new InvalidOperationException("Sin circuito de Blazor (simulado en el test).");

            return Task.FromResult(new AuthenticationState(_usuario ?? new ClaimsPrincipal(new ClaimsIdentity())));
        }
    }

    private sealed class HttpContextAccessorFalso(ClaimsPrincipal? usuario) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => usuario is null ? null : new DefaultHttpContext { User = usuario };
            set => throw new NotSupportedException();
        }
    }
}
