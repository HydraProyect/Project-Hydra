using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>Mismo fallo real que TenantActualTests, pero para el rol/Id de usuario que consume IAlcanceDatosService.</summary>
public class CurrentUserServiceTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("true", false)]
    [InlineData("-60", false)]
    [InlineData("hace-un-rato", false)]
    public async Task Totp_falla_en_cerrado_sin_una_marca_con_instante(string? marca, bool esperado)
    {
        var servicio = CrearServicio(new AuthenticationStateProviderFalso(PrincipalConMarcaTotp(marca)), new HttpContextAccessorFalso(null));
        (await servicio.SuperoTotpEnSesionAsync()).Should().Be(esperado);
    }

    [Fact]
    public async Task Totp_verificado_hace_un_momento_permite_abrir()
    {
        var marca = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var servicio = CrearServicio(new AuthenticationStateProviderFalso(PrincipalConMarcaTotp(marca)), new HttpContextAccessorFalso(null));
        (await servicio.SuperoTotpEnSesionAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(11 * 60, true)]
    [InlineData(12 * 60, true)]
    [InlineData(12 * 60 + 1, false)]
    [InlineData(14 * 24 * 60, false)]
    [InlineData(-4, true)]
    [InlineData(-6, false)]
    public void Totp_solo_es_reciente_dentro_de_la_ventana(int minutosDesdeLaVerificacion, bool esperado)
    {
        var ahora = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
        var marca = ahora.AddMinutes(-minutosDesdeLaVerificacion).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        SignInManagerCuentaDesactivada.TotpEsReciente(PrincipalConMarcaTotp(marca), ahora).Should().Be(esperado);
    }

    private static ClaimsPrincipal PrincipalConMarcaTotp(string? marca)
    {
        var identidad = new ClaimsIdentity("prueba");
        if (marca is not null)
            identidad.AddClaim(new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion, marca));
        return new ClaimsPrincipal(identidad);
    }

    private static readonly Guid UsuarioIdDeEjemplo = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public async Task Resuelve_rol_e_id_desde_el_circuito_de_blazor_cuando_hay_uno()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador"));
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var servicio = CrearServicio(authStateProvider, httpContextAccessor);

        (await servicio.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo);
        (await servicio.ObtenerRolEfectivoAsync()).Should().Be("Administrador");
    }

    [Fact]
    public async Task Cae_a_HttpContext_cuando_no_hay_circuito_de_blazor_pero_si_usuario_autenticado_por_cookie()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador"));
        var servicio = CrearServicio(authStateProvider, httpContextAccessor);

        (await servicio.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo);
        (await servicio.ObtenerRolEfectivoAsync()).Should().Be("Administrador");
    }

    [Fact]
    public async Task Devuelve_null_si_no_hay_circuito_ni_HttpContext_autenticado()
    {
        var authStateProvider = new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true);
        var httpContextAccessor = new HttpContextAccessorFalso(null);
        var servicio = CrearServicio(authStateProvider, httpContextAccessor);

        (await servicio.ObtenerUsuarioActualIdAsync()).Should().BeNull();
        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Un_circuito_con_la_sesion_invalidada_no_recupera_usuario_por_el_HttpContext_que_lo_abrio()
    {
        var httpContextAccessor = new HttpContextAccessorFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador"));

        var vigente = CrearServicio(new AuthenticationStateProviderFalso(lanzarInvalidOperationException: true), httpContextAccessor);
        var vigenteConInterfaz = CrearServicio(new ProveedorDeCircuitoInvalidadoFalso(sesionInvalidada: false), httpContextAccessor);
        var invalidado = CrearServicio(new ProveedorDeCircuitoInvalidadoFalso(), httpContextAccessor);

        (await vigente.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo, "control positivo: sin invalidar, el fallback sigue funcionando");
        (await vigenteConInterfaz.ObtenerUsuarioActualIdAsync()).Should().Be(UsuarioIdDeEjemplo, "el proveedor implementa la interfaz pero su sesión sigue vigente");
        (await invalidado.ObtenerUsuarioActualIdAsync()).Should().BeNull();
        (await invalidado.ObtenerRolEfectivoAsync()).Should().BeNull();
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
        (await servicio.ObtenerRolEfectivoAsync()).Should().BeNull();
    }

    /// <summary>
    /// El rol de origen sale de una proyección de la cuenta del Actor real, también en una Sesión
    /// Privilegiada: la selección no cambia de cuenta (el token no lleva Usuario simulado) y lo que se lee no
    /// trae credenciales. El orden hace determinista un estado que no debería existir.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task El_rol_de_origen_se_proyecta_de_la_cuenta_del_Actor_real(bool enSesionPrivilegiada)
    {
        var lectura = new LecturaCuentaFalsa(new EstadoCuentaPropia(false, false, ["GestorCae", "Administrador"]));
        var servicios = new ServiceCollection().AddSingleton<ILecturaCuentaPropia>(lectura).BuildServiceProvider();
        var servicio = new CurrentUserService(
            new AuthenticationStateProviderFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador")),
            new HttpContextAccessorFalso(null),
            enSesionPrivilegiada ? new ClienteActivoSeleccionadoConSesionPrivilegiadaFalso() : new ClienteActivoSeleccionadoFalso(),
            servicios);

        (await servicio.ObtenerRolOrigenAsync()).Should().Be("Administrador");
        lectura.Preguntadas.Should().Equal(UsuarioIdDeEjemplo);
    }

    [Fact]
    public async Task Sin_cuenta_visible_no_hay_rol_de_origen()
    {
        var servicios = new ServiceCollection().AddSingleton<ILecturaCuentaPropia>(new LecturaCuentaFalsa(null)).BuildServiceProvider();
        var servicio = new CurrentUserService(
            new AuthenticationStateProviderFalso(UsuarioAutenticadoCon(UsuarioIdDeEjemplo, "Administrador")),
            new HttpContextAccessorFalso(null), new ClienteActivoSeleccionadoFalso(), servicios);

        (await servicio.ObtenerRolOrigenAsync()).Should().BeNull();
    }

    private sealed class LecturaCuentaFalsa(EstadoCuentaPropia? estado) : ILecturaCuentaPropia
    {
        public List<Guid> Preguntadas { get; } = [];

        public Task<string?> LeerRolAsync(Guid usuarioId, CancellationToken ct = default)
        {
            Preguntadas.Add(usuarioId);
            return Task.FromResult(estado?.Roles.Order(StringComparer.Ordinal).FirstOrDefault());
        }

        public Task<EstadoCuentaPropia?> ObtenerAsync(Guid usuarioId, CancellationToken ct = default)
        {
            Preguntadas.Add(usuarioId);
            return Task.FromResult(estado);
        }
    }

    /// <summary>
    /// EquipoDelOperadorCaeExterno (quién revoca el acceso de un Operador CAE externo)
    /// pregunta el rol con el ámbito del Tenant de origen abierto. Ese ámbito no puede devolverle
    /// a una Sesión Privilegiada el rol de su casa: Soporte TALVEG no es Gestor CAE ni Operador CAE.
    /// Control positivo: sin Sesión Privilegiada, el mismo ámbito devuelve el rol de origen.
    /// </summary>
    [Fact]
    public async Task El_ambito_del_tenant_de_origen_no_devuelve_rol_a_una_sesion_privilegiada()
    {
        var tenantOrigen = Guid.Parse("55555555-5555-5555-5555-555555555555");
        ClaimsPrincipal EnSuOrigen() => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, UsuarioIdDeEjemplo.ToString()),
            new Claim(ClaimTypes.Role, "Administrador"),
            new Claim(CaeManager.Infrastructure.Identity.TenantClaimsPrincipalFactory.TipoClaimTenantId, tenantOrigen.ToString()),
        ], "prueba"));
        var privilegiada = new CurrentUserService(
            new AuthenticationStateProviderFalso(EnSuOrigen()),
            new HttpContextAccessorFalso(null),
            new ClienteActivoSeleccionadoConSesionPrivilegiadaFalso(),
            new ServiceCollection().BuildServiceProvider());
        var normal = CrearServicio(
            new AuthenticationStateProviderFalso(EnSuOrigen()),
            new HttpContextAccessorFalso(null));

        using (AmbitoTenantExplicito.Establecer(tenantOrigen))
        {
            (await privilegiada.ObtenerRolEfectivoAsync()).Should().BeNull();
            (await normal.ObtenerRolEfectivoAsync()).Should().Be("Administrador");
        }
    }

    /// <summary>
    /// Sin Delegated Workspace seleccionado, que es el caso de todo usuario
    /// que no es Operador Delegado: <c>ObtenerRolEfectivoAsync</c> devuelve el
    /// rol de la sesión en origen —el claim de rol, mientras ningún middleware
    /// lo haya sustituido— sin resolver nada del contenedor ni tocar la base de
    /// datos, por eso basta un proveedor vacío (ver CurrentUserService). El
    /// principal ya sustituido que pierde la selección se cubre en
    /// RolEfectivoDelWorkspaceMiddlewareTests; el camino delegado, en
    /// CaeManager.IntegrationTests, con contexto real.
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

    private sealed class ProveedorDeCircuitoInvalidadoFalso(bool sesionInvalidada = true)
        : AuthenticationStateProvider, ISesionDeCircuitoInvalidable
    {
        public bool SesionInvalidada => sesionInvalidada;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
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
