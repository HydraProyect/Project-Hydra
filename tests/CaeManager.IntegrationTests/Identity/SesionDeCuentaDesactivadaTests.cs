using System.Data.Common;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using CaeManager.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Identity;

/// <summary>
/// <c>SignInManagerCuentaDesactivada</c> contra Identity y PostgreSQL de verdad: una
/// cuenta desactivada deja de tener sesión de cookie, aunque su stamp no
/// cambie, y no puede obtener una nueva.
///
/// <para>
/// Es la capa que garantiza la propiedad (Infrastructure/Identity): el
/// <c>SecurityStampValidator</c> de la cookie y el proveedor de autenticación
/// del circuito llaman a <c>ValidateSecurityStampAsync(principal)</c>, y esa
/// llamada es la que se ejerce aquí, sobre el usuario releído de la base — que
/// es donde <c>LockoutEnd = MaxValue</c> viaja como <c>infinity</c> y vuelve
/// como lo que Npgsql decida. Un test con un usuario en memoria no vería ese
/// viaje. Que la pantalla de Usuarios rote el stamp, y que un circuito y una
/// cookie reales dejen de funcionar, lo ejerce el E2E
/// (<c>DesactivarCortaLaSesionTests</c>).
/// </para>
///
/// <para>
/// Cada rechazo tiene su control positivo al lado (la misma llamada, una
/// cuenta activa, devuelve el usuario) y el bloqueo temporal por intentos
/// fallidos se prueba aparte: NO debe cortar sesiones, o cualquiera con un
/// correo ajeno expulsaría a esa persona con cinco contraseñas erróneas.
/// </para>
/// </summary>
public class SesionDeCuentaDesactivadaTests(ITestOutputHelper salida) : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly ContadorDeConsultas _consultas = new();
    private ServiceProvider _servicios = null!;
    private Guid _tenant;

    public async Task InitializeAsync()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddHttpContextAccessor();
        servicios.AddSingleton<ITenantActual>(new TenantActualFijo());
        servicios.AddScoped<PuertaAccesoDatos>();
        servicios.AddSingleton(_consultas);

        servicios.AddDbContext<CaeManagerDbContext>((sp, opciones) => opciones
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(sp.GetRequiredService<ContadorDeConsultas>()));
        servicios.AddScoped<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());

        // Los tipos REALES que registra InfrastructureServiceCollectionExtensions
        // (SignInManagerCuentaDesactivada y la fábrica de claims por tenant): sustituirlos
        // sería probar el test.
        servicios.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        servicios.AddIdentityCore<ApplicationUser>()
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<CaeManagerDbContext>()
            .AddSignInManager<SignInManagerCuentaDesactivada>()
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>();

        // Intervalo mínimo para que el proveedor del circuito complete ciclos en el test.
        servicios.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromSeconds(1));

        _servicios = servicios.BuildServiceProvider();

        using var ambito = _servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.MigrateAsync();

        var tenant = new Tenant("Tenant de prueba");
        contexto.Tenants.Add(tenant);
        await contexto.SaveChangesAsync();
        _tenant = tenant.Id;

        var roleManager = ambito.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach (var rol in Roles.Todos)
            await roleManager.CreateAsync(new IdentityRole<Guid>(rol));
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_cuenta_activa_valida_su_sesion_y_la_desactivada_no()
    {
        var usuario = await CrearUsuarioAsync("control@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        (await ValidarAsync(principal)).Should().BeTrue("control positivo: el instrumento sí ve una sesión válida");

        await DesactivarSinRotarStampAsync(usuario.Id);

        (await ValidarAsync(principal)).Should().BeFalse(
            "el stamp sigue idéntico, así que solo el predicado de cuenta desactivada puede rechazarla");
    }

    [Fact]
    public async Task Rotar_el_stamp_tambien_invalida_y_es_una_causa_distinta()
    {
        var usuario = await CrearUsuarioAsync("stamp@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        using (var ambito = _servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var recargado = await userManager.FindByIdAsync(usuario.Id.ToString());
            (await userManager.UpdateSecurityStampAsync(recargado!)).Succeeded.Should().BeTrue();
        }

        (await ValidarAsync(principal)).Should().BeFalse("sensibilidad: el mismo instrumento ve el rechazo por stamp");
    }

    [Fact]
    public async Task Un_bloqueo_temporal_por_intentos_fallidos_no_corta_la_sesion()
    {
        var usuario = await CrearUsuarioAsync("temporal@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        using (var ambito = _servicios.CreateScope())
        {
            var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var recargado = await userManager.FindByIdAsync(usuario.Id.ToString());
            await userManager.SetLockoutEnabledAsync(recargado!, true);
            await userManager.SetLockoutEndDateAsync(recargado!, DateTimeOffset.UtcNow.AddMinutes(15));
        }

        (await ValidarAsync(principal)).Should().BeTrue(
            "si un bloqueo de 15 minutos cortara sesiones, cinco contraseñas erróneas de un tercero expulsarían a cualquiera");
    }

    [Fact]
    public async Task Una_cuenta_desactivada_no_recibe_cookie_nueva_y_una_activa_si()
    {
        var activa = await CrearUsuarioAsync("activa@x.test");
        var desactivada = await CrearUsuarioAsync("desactivada@x.test");

        // Cookies de ANTES de desactivar: RefreshSignInAsync solo reemite si
        // parte de una cookie autenticada (medido: sin ella no emite nada).
        var cookieActiva = await EmitirCookieAsync(activa.Id, refrescar: false, cookiePrevia: null);
        var cookieDesactivada = await EmitirCookieAsync(desactivada.Id, refrescar: false, cookiePrevia: null);
        cookieActiva.Should().NotBeEmpty("control positivo: SignInAsync sí emite cookie");
        cookieDesactivada.Should().NotBeEmpty();

        await DesactivarSinRotarStampAsync(desactivada.Id);

        (await EmitirCookieAsync(activa.Id, refrescar: true, cookiePrevia: cookieActiva))
            .Should().NotBeEmpty("control positivo: RefreshSignInAsync con cookie previa sí reemite cookie");

        // Es el bypass real: dentro de la ventana antes de la primera
        // revalidación, cambiar la contraseña llama a RefreshSignInAsync y
        // devolvía una cookie nueva con el stamp nuevo.
        (await EmitirCookieAsync(desactivada.Id, refrescar: true, cookiePrevia: cookieDesactivada)).Should().BeEmpty(
            "una cuenta desactivada no debe obtener cookie nueva por RefreshSignInAsync (cambio de contraseña)");
        (await EmitirCookieAsync(desactivada.Id, refrescar: false, cookiePrevia: null)).Should().BeEmpty(
            "ni por SignInAsync (contraseña, 2FA, callback de Microsoft)");
    }

    [Fact]
    public async Task Desactivar_rota_el_security_stamp_y_reactivar_no_lo_rota_de_nuevo()
    {
        // Rotación del stamp: solo la satisface ApplicationUser.Desactivar (el
        // bloqueo no toca el stamp). Reactivar no debe volver a rotarlo: el
        // que rotó al desactivar es el que mantiene muertas las sesiones viejas.
        var usuario = await CrearUsuarioAsync("rota@x.test");
        var antes = await StampAsync(usuario.Id);

        await CambiarActivacionAsync(usuario.Id, activar: false);
        var trasDesactivar = await StampAsync(usuario.Id);
        trasDesactivar.Should().NotBe(antes, "desactivar tiene que invalidar lo emitido antes, y eso lo hace el stamp");

        await CambiarActivacionAsync(usuario.Id, activar: true);
        (await StampAsync(usuario.Id)).Should().Be(trasDesactivar);
    }

    [Fact]
    public async Task Una_sesion_de_antes_no_resucita_al_reactivar_la_cuenta()
    {
        // Solo lo satisface la rotación del stamp: al reactivar, el rechazo por
        // «cuenta desactivada» ya no existe.
        var usuario = await CrearUsuarioAsync("no-resucita@x.test");
        var cookieDeAntes = await PrincipalDeSesionAsync(usuario);
        (await ValidarAsync(cookieDeAntes)).Should().BeTrue("control positivo: válida antes de desactivar");

        await CambiarActivacionAsync(usuario.Id, activar: false);
        (await ValidarAsync(cookieDeAntes)).Should().BeFalse("desactivada: cortada");

        await CambiarActivacionAsync(usuario.Id, activar: true);

        (await ValidarAsync(cookieDeAntes)).Should().BeFalse("reactivar no resucita la sesión anterior");
        (await ValidarAsync(await PrincipalDeSesionAsync(await ReadAsync(usuario.Id)))).Should().BeTrue(
            "control positivo: una sesión emitida después de reactivar sí es válida");
    }

    [Fact]
    public async Task El_validador_real_de_la_cookie_rechaza_una_cuenta_desactivada_con_el_stamp_intacto()
    {
        // El mismo ISecurityStampValidator que ejecuta el middleware de la
        // cookie, con la cookie «vencida» (emitida hace más que el intervalo):
        // demuestra que el hilo cookie → SignInManagerCuentaDesactivada está cableado, no
        // solo que el método del SignInManager haga lo esperado si se le llama.
        var usuario = await CrearUsuarioAsync("cookie-real@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);
        (await ValidarConElValidadorDeLaCookieAsync(principal)).Should().BeTrue("control positivo");

        await DesactivarSinRotarStampAsync(usuario.Id);

        (await ValidarConElValidadorDeLaCookieAsync(principal)).Should().BeFalse();
    }

    [Fact]
    public async Task El_circuito_abierto_pasa_a_anonimo_al_desactivar_la_cuenta_y_no_antes()
    {
        // El componente REAL con su temporizador real: un circuito conectado no
        // genera peticiones HTTP, así que solo este bucle puede enterarse.
        var usuario = await CrearUsuarioAsync("circuito-vivo@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        var proveedor = new ProveedorAutenticacionRevalidada(
            _servicios.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            _servicios.GetRequiredService<IServiceScopeFactory>(),
            _servicios.GetRequiredService<IOptions<SecurityStampValidatorOptions>>());
        try
        {
            proveedor.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));

            // Control positivo: el bucle corre (más de un ciclo) y no expulsa a una cuenta válida.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeTrue();
            proveedor.SesionInvalidada.Should().BeFalse("control positivo: una sesión válida no queda marcada como invalidada");

            await CambiarActivacionAsync(usuario.Id, activar: false);

            var limite = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < limite
                   && (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated)
                await Task.Delay(200);

            (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeFalse(
                "tras desactivar, el circuito abierto debe perder la autenticación en el siguiente ciclo");
            proveedor.SesionInvalidada.Should().BeTrue(
                "TenantActual y CurrentUserService lo consultan para no recuperar la identidad por el HttpContext heredado");
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    [Fact]
    public async Task Una_cancelacion_que_no_es_del_ciclo_no_expulsa_al_circuito()
    {
        // Un tiempo de espera del proveedor de base de datos llega como
        // OperationCanceledException con un token que NO es el del ciclo: el
        // bucle base la trataría como error y dejaría anónima a una cuenta
        // legítima. Debe conservar el estado, como cualquier otro fallo de la base.
        var usuario = await CrearUsuarioAsync("cancelacion-ajena@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        var proveedor = new ProveedorAutenticacionRevalidada(
            _servicios.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            new AmbitosQueSeCancelan(),
            _servicios.GetRequiredService<IOptions<SecurityStampValidatorOptions>>());
        try
        {
            proveedor.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));

            await Task.Delay(TimeSpan.FromSeconds(3.5)); // al menos un ciclo completo con la cancelación ajena
            (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeTrue(
                "una cancelación ajena al ciclo es un fallo transitorio, no una sesión inválida");
            proveedor.SesionInvalidada.Should().BeFalse();
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    [Fact]
    public async Task La_cancelacion_del_propio_ciclo_se_relanza_con_su_token()
    {
        // El bucle base solo toma por cierre normal una cancelación con SU token;
        // con otro (p. ej. la de un tiempo de espera de la base que coincida con
        // el relevo de estado) fuerza el estado anónimo.
        var usuario = await CrearUsuarioAsync("cancelacion-propia@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);
        var proveedor = new ProveedorAutenticacionRevalidada(
            _servicios.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            new AmbitosQueSeCancelan(),
            _servicios.GetRequiredService<IOptions<SecurityStampValidatorOptions>>());
        try
        {
            using var ciclo = new CancellationTokenSource();
            ciclo.Cancel();

            var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod(
                "ValidateAuthenticationStateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var tarea = (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), ciclo.Token])!;

            var excepcion = await tarea.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
            excepcion.Which.CancellationToken.Should().Be(ciclo.Token,
                "una cancelación pedida por el ciclo debe salir con el token del ciclo, no con el de una excepción ajena");
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    [Fact]
    public async Task Coste_de_una_revalidacion_en_consultas_a_la_base()
    {
        var usuario = await CrearUsuarioAsync("coste@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        _consultas.Reiniciar();
        await ValidarAsync(principal);
        var soloValidar = _consultas.Total;

        // Lo que añade la cookie cuando la validación sale bien: reconstruir el
        // principal (roles, claims) para renovar el ticket.
        _consultas.Reiniciar();
        using (var ambito = _servicios.CreateScope())
        {
            var signInManager = ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
            var valido = await signInManager.ValidateSecurityStampAsync(principal);
            valido.Should().NotBeNull();
            await signInManager.CreateUserPrincipalAsync(valido!);
        }
        var conRenovacionDeCookie = _consultas.Total;

        salida.WriteLine($"CONSULTAS: revalidar circuito = {soloValidar}; revalidar cookie (validar + renovar principal) = {conRenovacionDeCookie}");

        // Presupuesto, no una medida exacta: el circuito lee el usuario una
        // vez por ciclo; la cookie, además, los roles. Si esto crece sin
        // querer, el coste por sesión activa y minuto deja de ser el medido.
        soloValidar.Should().BeLessThanOrEqualTo(1);
        conRenovacionDeCookie.Should().BeLessThanOrEqualTo(6);
    }

    private async Task<ApplicationUser> CrearUsuarioAsync(string email)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = email,
            TenantId = _tenant,
            LockoutEnabled = true,
        };

        (await userManager.CreateAsync(usuario)).Succeeded.Should().BeTrue();
        (await userManager.AddToRoleAsync(usuario, Roles.GestorCae)).Succeeded.Should().BeTrue();

        return (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
    }

    private async Task<ClaimsPrincipal> PrincipalDeSesionAsync(ApplicationUser usuario)
    {
        using var ambito = _servicios.CreateScope();
        var signInManager = ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        return await signInManager.CreateUserPrincipalAsync(usuario);
    }

    private async Task<bool> ValidarAsync(ClaimsPrincipal principal)
    {
        using var ambito = _servicios.CreateScope();
        var signInManager = ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        return await signInManager.ValidateSecurityStampAsync(principal) is not null;
    }

    /// <summary>
    /// Deja la cuenta exactamente como la dejaba «Desactivar» antes del arreglo
    /// (LockoutEnd = MaxValue y nada más), que es también como están las
    /// cuentas desactivadas antes del despliegue.
    /// </summary>
    private async Task DesactivarSinRotarStampAsync(Guid usuarioId)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
        usuario!.LockoutEnabled = true;
        usuario.LockoutEnd = DateTimeOffset.MaxValue;
        (await userManager.UpdateAsync(usuario)).Succeeded.Should().BeTrue();
    }

    private async Task CambiarActivacionAsync(Guid usuarioId, bool activar)
    {
        // Lo mismo que hace la pantalla /usuarios.
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = (await userManager.FindByIdAsync(usuarioId.ToString()))!;
        if (activar) usuario.Reactivar(); else usuario.Desactivar();
        (await userManager.UpdateAsync(usuario)).Succeeded.Should().BeTrue();
    }

    private async Task<string> StampAsync(Guid usuarioId) => (await ReadAsync(usuarioId)).SecurityStamp!;

    private async Task<ApplicationUser> ReadAsync(Guid usuarioId)
    {
        using var ambito = _servicios.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        return (await userManager.FindByIdAsync(usuarioId.ToString()))!;
    }

    /// <summary>
    /// Pasa el principal por el <c>ISecurityStampValidator</c> REAL con la
    /// cookie vencida, como haría el middleware. Devuelve si sigue aceptada.
    /// </summary>
    private async Task<bool> ValidarConElValidadorDeLaCookieAsync(ClaimsPrincipal principal)
    {
        using var ambito = _servicios.CreateScope();
        var http = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = http;

        var opciones = ambito.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var ticket = new AuthenticationTicket(
            principal,
            new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow.AddHours(-1),
                ExpiresUtc = DateTimeOffset.UtcNow.AddDays(13),
            },
            IdentityConstants.ApplicationScheme);
        var contexto = new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler)),
            opciones,
            ticket);

        await ambito.ServiceProvider.GetRequiredService<ISecurityStampValidator>().ValidateAsync(contexto);

        return contexto.Principal is not null;
    }

    /// <summary>Devuelve el valor de la cabecera Set-Cookie que produce RefreshSignInAsync (o SignInAsync), o vacío si no emitió nada.</summary>
    private async Task<string> EmitirCookieAsync(Guid usuarioId, bool refrescar, string? cookiePrevia)
    {
        using var ambito = _servicios.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        if (!string.IsNullOrEmpty(cookiePrevia))
            contexto.Request.Headers.Cookie = cookiePrevia.Split(';')[0]; // «nombre=valor», sin atributos
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;

        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());

        if (refrescar)
            await signInManager.RefreshSignInAsync(usuario!);
        else
            await signInManager.SignInAsync(usuario!, isPersistent: false);

        return contexto.Response.Headers.SetCookie.ToString();
    }

    private sealed class AmbitosQueSeCancelan : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new TaskCanceledException("tiempo de espera del proveedor");
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId { get; set; }
    }

    private sealed class ContadorDeConsultas : DbCommandInterceptor
    {
        private int _total;
        public int Total => Volatile.Read(ref _total);
        public void Reiniciar() => Interlocked.Exchange(ref _total, 0);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand comando, CommandEventData datos, InterceptionResult<DbDataReader> resultado,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _total);
            return base.ReaderExecutingAsync(comando, datos, resultado, cancellationToken);
        }
    }
}
