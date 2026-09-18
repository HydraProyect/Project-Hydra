using System.Security.Claims;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// El guard de seguridad de <see cref="MainLayout"/> (cambio de contraseña
/// forzoso, rol pendiente, 2FA obligatoria para Administrador) falla cerrado:
/// si no se pudo evaluar y el circuito sigue vivo, el contenido se retira.
///
/// <para>
/// Hasta 2026-09-18 el <c>catch</c> aceptaba <see cref="ObjectDisposedException"/>
/// y <see cref="ArgumentOutOfRangeException"/> (PR #517, la carrera de
/// desconexión de circuito) y terminaba sin hacer nada. Esos dos tipos también
/// los produce un fallo con el circuito vivo, y entonces la página se mostraba
/// sin que ninguna de las tres comprobaciones se hubiera aplicado.
/// </para>
///
/// <para>
/// La excepción se inyecta desde el <see cref="IUserStore{TUser}"/>, que es por
/// donde la sufre de verdad —<c>UserManager</c> por debajo—, y el circuito se
/// declara muerto por el único camino que lo declara en producción
/// (<see cref="EstadoDelCircuito.OnCircuitClosedAsync"/>), no por una bandera
/// de test.
/// </para>
///
/// <para>
/// El destino con el circuito vivo lleva una referencia de correlación y la
/// ruta de origen como parámetros de consulta (<c>MainLayout.ConDiagnostico</c>):
/// sin ellos, <c>/Error</c> se queda sin diagnóstico porque este camino no pasa
/// por <c>UseExceptionHandler</c>. Las aserciones comprueban el prefijo y los
/// parámetros, no una igualdad exacta de la URL.
/// </para>
/// </summary>
public class MainLayoutFallaCerradoTests : BunitContext
{
    private static readonly Guid IdUsuario = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly EstadoDelCircuito _circuito = new();
    private readonly AlmacenConmutable _almacen = new();
    private readonly NavegacionDeBanco _navegacion = new();
    private readonly ProveedorAutenticacionConmutable _autenticacion = new();

    /// <summary>
    /// Todo se registra aquí porque bUnit congela su proveedor de servicios en
    /// cuanto alguien resuelve el primero: los dobles no se sustituyen por test,
    /// se conmutan.
    /// </summary>
    public MainLayoutFallaCerradoTests()
    {
        // Solo MainLayout se monta de verdad: todo lo que cuelga de su markup
        // (NavMenu, selectores, popups, workspace...) son stubs. Lo que este
        // caso mide vive en OnParametersSetAsync, no en el árbol de hijos.
        ComponentFactories.Add(new TodoMenosElLayout());

        var usuarios = CrearUsuarios(_almacen);
        Services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        Services.AddSingleton(_circuito);
        Services.AddSingleton(new PuertaAccesoDatos());
        Services.AddSingleton(usuarios);
        Services.AddSingleton<ActividadUsuarioService>(new ActividadUsuarioSilenciosa(usuarios));
        Services.AddSingleton<NavigationManager>(_navegacion);

        // Doble propio en vez de AddAuthorization() de bUnit: MainLayout
        // inyecta AuthenticationStateProvider directamente (no lee un
        // CascadingParameter de estado de autenticación) y AuthorizeView está
        // stubbeado por TodoMenosElLayout, así que no hace falta el aparato de
        // autorización de bUnit — y este doble, a diferencia de aquel, puede
        // conmutarse para fallar (ver Con_la_llamada_previa_al_guard...).
        _autenticacion.AutenticarComo(IdUsuario, "gestora@refrielectric.test");
        Services.AddSingleton<AuthenticationStateProvider>(_autenticacion);

        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    /// <summary>
    /// El caso que el catch anterior daba por imposible: la excepción salta con
    /// el circuito vivo. Nadie sabe si a este usuario le tocaba cambiar la
    /// contraseña o configurar la 2FA, así que el contenido se retira.
    ///
    /// <para>
    /// <see cref="OperationCanceledException"/> entra en la misma lista, a
    /// propósito, tras un hallazgo de Codex sobre este incremento (P1): una
    /// versión anterior la excluía del catch pensando solo en la petición
    /// abortada, pero una dependencia del guard (p. ej. un timeout de consulta
    /// en <c>UserManager</c>/EF Core) puede lanzarla igual con el circuito
    /// perfectamente vivo, y desde aquí no hay forma fiable de distinguir ese
    /// caso del de la conexión perdida sin acoplar el guard al token interno
    /// de esa dependencia. Excluirla de forma general dejaba pasar ese caso
    /// sin redirigir y sin registrar nada — el mismo fallo abierto que este
    /// catch existe para cerrar. El caso realmente benigno —circuito ya
    /// cerrado— lo sigue cubriendo <c>EstadoDelCircuito.Cerrado</c>, no un
    /// filtro por tipo (ver
    /// <see cref="Con_el_circuito_ya_cerrado_la_excepcion_se_descarta_sin_redirigir"/>).
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(typeof(ObjectDisposedException))]
    [InlineData(typeof(ArgumentOutOfRangeException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(OperationCanceledException))]
    public void Con_el_circuito_vivo_una_excepcion_en_el_guard_retira_el_contenido(Type tipoDeExcepcion)
    {
        _almacen.Falla(tipoDeExcepcion);

        Render<MainLayout>();

        var destino = _navegacion.Destinos.Should().ContainSingle().Subject;
        var (ruta, consulta) = SepararRutaYConsulta(destino);
        ruta.Should().Be("/Error");
        consulta.Should().ContainKey("ref").WhoseValue.Should().NotBeNullOrEmpty();
        consulta.Should().ContainKey("ruta").WhoseValue.Should().NotBeNullOrEmpty(
            "sin la ruta de origen, /Error no tiene adónde reintentar (PaginaEstadoSistema.RutaReintentar)");
        _navegacion.ForzoLaCarga.Should().BeTrue(
            "una navegación interna conservaría el documento, y el contenido protegido seguiría a la vista");
    }

    /// <summary>
    /// El caso SSR: <c>EstadoDelCircuito.Cerrado</c> en <c>false</c> no
    /// significa siempre "hay un circuito vivo que podría haberse ido" — en el
    /// prerenderizado de <c>@rendermode InteractiveServer</c> (o en cualquier
    /// página sin ningún descendiente interactivo) MainLayout se ejecuta antes
    /// de que exista un circuito real, así que <c>Cerrado</c> nunca ha tenido
    /// ocasión de pasar a <c>true</c> — no porque el circuito se fuera, sino
    /// porque no lo hay. Ver el <c>remarks</c> de
    /// <see cref="EstadoDelCircuito.Cerrado"/>: aquí no hay carrera de
    /// desconexión que perdonar, así que una excepción del guard sigue siendo
    /// un fallo real y se redirige igual que con el circuito vivo — el mismo
    /// resultado que <see cref="Con_el_circuito_vivo_una_excepcion_en_el_guard_retira_el_contenido"/>,
    /// por un motivo distinto.
    /// </summary>
    [Fact]
    public void Durante_el_prerenderizado_sin_circuito_una_excepcion_tambien_retira_el_contenido()
    {
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));
        _almacen.Falla(typeof(ObjectDisposedException));

        Render<MainLayout>();

        var destino = _navegacion.Destinos.Should().ContainSingle().Subject;
        SepararRutaYConsulta(destino).Ruta.Should().Be("/Error");
    }

    /// <summary>
    /// El otro lado del contrato: con el circuito ya cerrado no hay nadie
    /// mirando la página, así que redirigir no protege a nadie — y la carrera
    /// de desconexión (Sentry DOTNET-3 y DOTNET-6) no puede convertirse en un
    /// error para el usuario siguiente.
    /// </summary>
    [Fact]
    public async Task Con_el_circuito_ya_cerrado_la_excepcion_se_descarta_sin_redirigir()
    {
        _almacen.Falla(typeof(ObjectDisposedException));
        await _circuito.OnCircuitClosedAsync(circuit: null!, CancellationToken.None);

        Render<MainLayout>();

        _navegacion.Destinos.Should().BeEmpty();
    }

    /// <summary>
    /// Control positivo del instrumento: sin excepción, el guard sigue haciendo
    /// su trabajo de siempre. Si este caso también acabara en <c>/Error</c>, el
    /// de arriba estaría midiendo "MainLayout siempre redirige", no el fallo
    /// cerrado.
    /// </summary>
    [Fact]
    public void Sin_excepcion_el_guard_sigue_redirigiendo_a_donde_le_toca()
    {
        _almacen.Devuelve(UsuarioQueDebeCambiarContrasena());

        Render<MainLayout>();

        _navegacion.Destinos.Should().ContainSingle().Which.Should().EndWith("/cuenta/cambiar-contrasena");
    }

    /// <summary>
    /// Segundo control positivo: un usuario en regla se queda donde está. Sin
    /// él, "redirige" y "no muestra el contenido" serían indistinguibles.
    /// </summary>
    [Fact]
    public void Un_usuario_en_regla_no_se_va_a_ninguna_parte()
    {
        _almacen.Devuelve(UsuarioEnRegla(), Roles.GestorCae);

        Render<MainLayout>();

        _navegacion.Destinos.Should().BeEmpty();
    }

    /// <summary>
    /// La trampa del propio arreglo. Al renderizar en el servidor,
    /// <c>NavigateTo</c> no vuelve: señala la redirección lanzando
    /// <see cref="NavigationException"/>. Un catch que la atrapara se tragaría
    /// el "cambia la contraseña" que el guard acaba de decidir y lo cambiaría
    /// por <c>/Error</c> — el mismo fallo abierto, reintroducido por el arreglo.
    /// </summary>
    [Fact]
    public void La_redireccion_del_guard_no_la_atrapa_el_catch()
    {
        _almacen.Devuelve(UsuarioQueDebeCambiarContrasena());
        _navegacion.LanzarComoElServidor = true;

        var montar = () => Render<MainLayout>();

        montar.Should().Throw<NavigationException>();
        _navegacion.Destinos.Should().ContainSingle().Which.Should().EndWith("/cuenta/cambiar-contrasena");
    }

    /// <summary>
    /// El paso previo al guard también puede fallar con el circuito vivo: si
    /// <c>AuthenticationStateProvider.GetAuthenticationStateAsync</c> lanza,
    /// antes de 2026-09-18 esa excepción escapaba del método entero sin pasar
    /// por ningún <c>catch</c> propio — el guard de contraseña/rol/2FA nunca
    /// llegaba a evaluarse, y con ella fuera del <c>try</c> tampoco había
    /// forma de decidir, por el estado del circuito, si tocaba retirar el
    /// contenido. Ahora esa llamada está dentro del mismo <c>try</c> que el
    /// resto del guard, así que sufre el mismo criterio.
    /// </summary>
    [Fact]
    public void Con_la_llamada_previa_al_guard_fallando_y_el_circuito_vivo_tambien_se_retira_el_contenido()
    {
        _autenticacion.Falla(new InvalidOperationException("el estado de autenticación no se pudo resolver"));

        Render<MainLayout>();

        var (ruta, _) = SepararRutaYConsulta(_navegacion.Destinos.Should().ContainSingle().Subject);
        ruta.Should().Be("/Error");
    }

    private static (string Ruta, Dictionary<string, string?> Consulta) SepararRutaYConsulta(string destinoAbsoluto)
    {
        var uri = new Uri(destinoAbsoluto);
        var consulta = QueryHelpers.ParseQuery(uri.Query)
            .ToDictionary(par => par.Key, par => (string?)par.Value.ToString());
        return (uri.AbsolutePath, consulta);
    }

    private static ApplicationUser UsuarioQueDebeCambiarContrasena() =>
        new() { Id = IdUsuario, UserName = "gestora@refrielectric.test", DebeCambiarContrasena = true };

    private static ApplicationUser UsuarioEnRegla() =>
        new() { Id = IdUsuario, UserName = "gestora@refrielectric.test", DebeCambiarContrasena = false };

    private static UserManager<ApplicationUser> CrearUsuarios(IUserStore<ApplicationUser> almacen) => new(
        almacen, Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance);

    /// <summary>Monta MainLayout de verdad y sustituye por un stub todo lo demás.</summary>
    private sealed class TodoMenosElLayout : IComponentFactory
    {
        public bool CanCreate(Type componentType) => componentType != typeof(MainLayout);

        public IComponent Create(Type componentType) =>
            (IComponent)Activator.CreateInstance(typeof(Stub<>).MakeGenericType(componentType))!;
    }

    /// <summary>
    /// El resumen de ausencia no es lo que se mide aquí, y su implementación
    /// real exige un UserManager que escriba de verdad (ver el remarks de
    /// <see cref="ActividadUsuarioService.RegistrarYEvaluarAsync"/>).
    /// </summary>
    private sealed class ActividadUsuarioSilenciosa(UserManager<ApplicationUser> usuarios)
        : ActividadUsuarioService(null!, usuarios, new PuertaAccesoDatos())
    {
        public override Task<(bool Ausente, DateTime? DesdeParaResumen)> RegistrarYEvaluarAsync(
            bool interactivo, CancellationToken cancellationToken = default) =>
            Task.FromResult<(bool, DateTime?)>((false, null));
    }

    /// <summary>
    /// Registra adónde se navega y con qué opciones, y sabe abortar como el
    /// <see cref="NavigationManager"/> del renderizado del servidor, que señala
    /// la redirección lanzando <see cref="NavigationException"/> en vez de
    /// volver. El de bUnit no lanza, así que sin esto la exclusión de esa
    /// excepción en el catch de MainLayout no tendría quien la ejercitara.
    /// </summary>
    private sealed class NavegacionDeBanco : NavigationManager
    {
        public NavegacionDeBanco() => Initialize("http://localhost/", "http://localhost/inicio");

        public List<string> Destinos { get; } = [];

        public bool ForzoLaCarga { get; private set; }

        public bool LanzarComoElServidor { get; set; }

        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            var absoluta = ToAbsoluteUri(uri).ToString();
            Destinos.Add(absoluta);
            ForzoLaCarga = forceLoad;
            if (LanzarComoElServidor) throw new NavigationException(absoluta);
        }
    }

    /// <summary>
    /// Autenticado como el mismo usuario para todos los tests salvo que se le
    /// pida fallar. <c>Falla</c> hace que <see cref="GetAuthenticationStateAsync"/>
    /// lance directamente, el paso que hasta 2026-09-18 vivía fuera del
    /// <c>try</c> del guard.
    /// </summary>
    private sealed class ProveedorAutenticacionConmutable : AuthenticationStateProvider
    {
        private ClaimsPrincipal _principal = new(new ClaimsIdentity());
        private Exception? _excepcion;

        public void AutenticarComo(Guid id, string nombreUsuario) => _principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Name, nombreUsuario)],
                authenticationType: "prueba"));

        public void Falla(Exception excepcion) => _excepcion = excepcion;

        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            _excepcion is { } excepcion ? throw excepcion : Task.FromResult(new AuthenticationState(_principal));
    }

    /// <summary>
    /// Solo lo que el guard de MainLayout toca —buscar por id, roles y 2FA—, y
    /// conmutable porque bUnit no deja registrar servicios nuevos una vez
    /// empezado el test.
    /// </summary>
    private sealed class AlmacenConmutable : IUserStore<ApplicationUser>, IUserRoleStore<ApplicationUser>, IUserTwoFactorStore<ApplicationUser>
    {
        private Type? _tipoDeExcepcion;
        private ApplicationUser? _usuario;
        private IList<string> _roles = [];

        public void Falla(Type tipoDeExcepcion) => _tipoDeExcepcion = tipoDeExcepcion;

        public void Devuelve(ApplicationUser usuario, params string[] roles)
        {
            _usuario = usuario;
            _roles = roles;
        }

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct)
        {
            if (_tipoDeExcepcion is not null) throw (Exception)Activator.CreateInstance(_tipoDeExcepcion)!;
            return Task.FromResult(_usuario?.Id.ToString() == userId ? _usuario : null);
        }

        public Task<IList<string>> GetRolesAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(_roles);

        public Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user, CancellationToken ct) =>
            Task.FromResult(user.TwoFactorEnabled);

        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) { user.UserName = userName; return Task.CompletedTask; }
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) { user.NormalizedUserName = normalizedName; return Task.CompletedTask; }

        public void Dispose() { }
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
        public Task AddToRoleAsync(ApplicationUser user, string roleName, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveFromRoleAsync(ApplicationUser user, string roleName, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> IsInRoleAsync(ApplicationUser user, string roleName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IList<ApplicationUser>> GetUsersInRoleAsync(string roleName, CancellationToken ct) => throw new NotSupportedException();
        public Task SetTwoFactorEnabledAsync(ApplicationUser user, bool enabled, CancellationToken ct) => throw new NotSupportedException();
    }
}
