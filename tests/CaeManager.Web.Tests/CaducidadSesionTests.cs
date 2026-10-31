using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Caducidad de la sesión (ver <see cref="CaducidadSesion"/>): 30 min sin interacción,
/// tope de 12 h desde el inicio que ni el refresco deslizante ni una reemisión con las
/// propiedades del ticket reinician, cookie no persistente y el circuito de Blazor
/// cortado en fallo cerrado. El recorrido de extremo a extremo usa el manejador de
/// cookies real sobre Kestrel con un reloj manual.
/// </summary>
public class CaducidadSesionTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Hsts_dura_un_anyo()
    {
        var servicios = new ServiceCollection();
        CaducidadSesion.ConfigurarHsts(servicios);
        using var proveedor = servicios.BuildServiceProvider();
        proveedor.GetRequiredService<IOptions<HstsOptions>>().Value.MaxAge.Should().Be(TimeSpan.FromDays(365));
    }

    [Theory]
    [InlineData("Production", CookieSecurePolicy.Always)]
    [InlineData("Staging", CookieSecurePolicy.Always)]
    [InlineData("Escaneo", CookieSecurePolicy.Always)]
    [InlineData("Development", CookieSecurePolicy.SameAsRequest)]
    public void Configura_la_cookie(string entorno, CookieSecurePolicy secure)
    {
        var opciones = new CookieAuthenticationOptions();
        CaducidadSesion.Configurar(opciones, Entorno(entorno));
        opciones.ExpireTimeSpan.Should().Be(TimeSpan.FromHours(8));
        opciones.SlidingExpiration.Should().BeTrue();
        opciones.Cookie.SecurePolicy.Should().Be(secure);
        opciones.Cookie.HttpOnly.Should().BeTrue();
        opciones.Cookie.SameSite.Should().NotBe(SameSiteMode.None);
    }

    [Fact]
    public async Task La_emision_y_la_revalidacion_conservan_el_inicio_original()
    {
        var opciones = new CookieAuthenticationOptions();
        // Como el SecurityStampValidator: reemplaza el principal por uno sin la marca.
        opciones.Events.OnValidatePrincipal = contexto =>
        {
            contexto.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity("cookie")));
            return Task.CompletedTask;
        };
        CaducidadSesion.Configurar(opciones, Entorno("Production"));
        var reloj = new RelojManual(Ahora);
        using var servicios = new ServiceCollection().AddSingleton<TimeProvider>(reloj).BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = servicios };
        var esquema = new AuthenticationScheme("aplicacion", null, typeof(CookieAuthenticationHandler));
        var propiedades = new AuthenticationProperties { IsPersistent = true };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([ClaimSid()], "cookie"));
        await opciones.Events.OnSigningIn(new CookieSigningInContext(http, esquema, opciones, principal, propiedades, new CookieOptions()));
        propiedades.Items[CaducidadSesion.MarcaInicio].Should().Be(Marca(Ahora));
        principal.FindFirst(CaducidadSesion.MarcaInicio)!.Value.Should().Be(Marca(Ahora));
        principal.FindFirst(CaducidadSesion.MarcaInteraccion)!.Value.Should().Be(Marca(Ahora));
        propiedades.IsPersistent.Should().BeFalse("ningún camino de inicio de sesión deja una cookie persistente");

        reloj.Ahora = Ahora.AddMinutes(20);
        var validar = new CookieValidatePrincipalContext(http, esquema, opciones, new AuthenticationTicket(principal, propiedades, esquema.Name));
        await opciones.Events.OnValidatePrincipal(validar);
        validar.Principal!.FindFirst(CaducidadSesion.MarcaInicio)!.Value.Should().Be(Marca(Ahora));
        validar.Principal!.FindFirst(CaducidadSesion.MarcaInteraccion)!.Value.Should().Be(Marca(Ahora),
            "una petición cualquiera no es una interacción");

        // RefreshSignInAsync reemite con las propiedades del ticket vigente.
        var renovado = new ClaimsPrincipal(new ClaimsIdentity("cookie"));
        await opciones.Events.OnSigningIn(new CookieSigningInContext(http, esquema, opciones, renovado, propiedades, new CookieOptions()));
        renovado.FindFirst(CaducidadSesion.MarcaInicio)!.Value.Should().Be(Marca(Ahora));
    }

    [Theory]
    [InlineData(719, true)]
    [InlineData(720, false)]
    [InlineData(721, false)]
    public async Task La_cookie_respeta_el_inicio_original_aunque_haya_actividad(int minutos, bool vigente)
    {
        var opciones = new CookieAuthenticationOptions();
        var llamadas = 0;
        opciones.Events.OnValidatePrincipal = _ => { llamadas++; return Task.CompletedTask; };
        CaducidadSesion.Configurar(opciones, Entorno("Production"));
        var servicios = new ServiceCollection().AddSingleton<TimeProvider>(new RelojManual(Ahora))
            .AddSingleton<IAuthenticationService, AutenticacionFalsa>().BuildServiceProvider();
        await using (servicios)
        {
            var http = new DefaultHttpContext { RequestServices = servicios };
            var propiedades = new AuthenticationProperties { IssuedUtc = Ahora.AddMinutes(-1), IsPersistent = true };
            propiedades.Items[CaducidadSesion.MarcaInicio] = Marca(Ahora.AddMinutes(-minutos));
            var principal = new ClaimsPrincipal(new ClaimsIdentity([ClaimSid()], "cookie"));
            var esquema = new AuthenticationScheme("aplicacion", null, typeof(CookieAuthenticationHandler));
            var contexto = new CookieValidatePrincipalContext(http, esquema, opciones, new AuthenticationTicket(principal, propiedades, esquema.Name));
            await opciones.Events.OnValidatePrincipal(contexto);
            (contexto.Principal is not null).Should().Be(vigente);
            llamadas.Should().Be(vigente ? 1 : 0);
            servicios.GetRequiredService<IAuthenticationService>().As<AutenticacionFalsa>().Salidas.Should().Be(vigente ? 0 : 1);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no-es-un-numero")]
    [InlineData("-5")]
    public async Task Una_cookie_sin_inicio_legible_se_rechaza(string? marca)
    {
        var opciones = new CookieAuthenticationOptions();
        CaducidadSesion.Configurar(opciones, Entorno("Production"));
        var servicios = new ServiceCollection().AddSingleton<TimeProvider>(new RelojManual(Ahora))
            .AddSingleton<IAuthenticationService, AutenticacionFalsa>().BuildServiceProvider();
        await using (servicios)
        {
            var http = new DefaultHttpContext { RequestServices = servicios };
            var propiedades = new AuthenticationProperties();
            if (marca is not null) propiedades.Items[CaducidadSesion.MarcaInicio] = marca;
            var esquema = new AuthenticationScheme("aplicacion", null, typeof(CookieAuthenticationHandler));
            var contexto = new CookieValidatePrincipalContext(http, esquema, opciones,
                new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("cookie")), propiedades, esquema.Name));
            await opciones.Events.OnValidatePrincipal(contexto);
            contexto.Principal.Should().BeNull();
        }
    }

    /// <summary>
    /// La cookie emitida antes de la revocación en servidor no lleva identificador de sesión:
    /// se rechaza y se borra en la primera petición, aunque su hora de inicio valga, y sin llegar
    /// al SecurityStampValidator.
    /// </summary>
    [Fact]
    public async Task Una_cookie_sin_identificador_de_sesion_se_rechaza_y_se_borra()
    {
        var opciones = new CookieAuthenticationOptions();
        var llamadas = 0;
        opciones.Events.OnValidatePrincipal = _ => { llamadas++; return Task.CompletedTask; };
        CaducidadSesion.Configurar(opciones, Entorno("Production"));
        var servicios = new ServiceCollection().AddSingleton<TimeProvider>(new RelojManual(Ahora))
            .AddSingleton<IAuthenticationService, AutenticacionFalsa>().BuildServiceProvider();
        await using (servicios)
        {
            var http = new DefaultHttpContext { RequestServices = servicios };
            var propiedades = new AuthenticationProperties { IssuedUtc = Ahora.AddMinutes(-1), IsPersistent = true };
            propiedades.Items[CaducidadSesion.MarcaInicio] = Marca(Ahora.AddMinutes(-5));
            var esquema = new AuthenticationScheme("aplicacion", null, typeof(CookieAuthenticationHandler));
            var contexto = new CookieValidatePrincipalContext(http, esquema, opciones, new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "cookie")), propiedades, esquema.Name));
            await opciones.Events.OnValidatePrincipal(contexto);
            contexto.Principal.Should().BeNull();
            llamadas.Should().Be(0);
            servicios.GetRequiredService<IAuthenticationService>().As<AutenticacionFalsa>().Salidas.Should().Be(1);
        }
    }

    [Fact]
    public async Task El_circuito_sin_identificador_de_sesion_falla_cerrado_sin_consultar_la_base()
    {
        // Sin SignInManager registrado: llegar a la base dejaría un aviso de error en el log.
        var registro = new RegistroDeAvisos();
        var servicios = new ServiceCollection().BuildServiceProvider();
        using var proveedor = new ProveedorAutenticacionRevalidada(new FabricaDeRegistro(registro),
            servicios.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SecurityStampValidatorOptions()), new RelojManual(Ahora));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(CaducidadSesion.MarcaInicio, Marca(Ahora.AddMinutes(-5)))], "cookie"));
        var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        (await (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), CancellationToken.None])!).Should().BeFalse();
        proveedor.SesionInvalidada.Should().BeTrue();
        registro.Avisos.Should().Be(0);
    }

    private static Claim ClaimSid() =>
        new(CaeManager.Infrastructure.Identity.SignInManagerCuentaDesactivada.ClaimSid, Guid.NewGuid().ToString("N"));

    public enum Reemision { PorSenal, ConPropiedadesDelTicket }

    /// <summary>
    /// Manejador de cookies real, con interacción cada 20 min: cada señal reemite la cookie
    /// (y a las 5 h también como <c>RefreshSignInAsync</c>), a las 11 h 59 min sigue
    /// valiendo y a las 12 h 1 min, con la cookie más reciente, se rechaza y se borra.
    /// </summary>
    [Theory]
    [InlineData(Reemision.PorSenal)]
    [InlineData(Reemision.ConPropiedadesDelTicket)]
    public async Task Reemitir_la_cookie_no_reinicia_el_tope_de_doce_horas(Reemision reemision)
    {
        var reloj = new RelojManual(Ahora);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(reloj);
        var sid = ClaimSid();
        builder.Services.AddAuthentication("aplicacion").AddCookie("aplicacion");
        builder.Services.Configure<CookieAuthenticationOptions>("aplicacion",
            opciones => CaducidadSesion.Configurar(opciones, builder.Environment));
        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/entrar", (HttpContext contexto) => contexto.SignInAsync("aplicacion",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1"), sid], "aplicacion")),
            new AuthenticationProperties { IsPersistent = true }));
        app.MapGet("/refrescar", async (HttpContext contexto) =>
        {
            var actual = await contexto.AuthenticateAsync("aplicacion");
            await contexto.SignInAsync("aplicacion",
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1"), sid], "aplicacion")), actual.Properties);
        });
        app.MapGet("/yo", (HttpContext contexto) => contexto.User.Identity?.IsAuthenticated == true
            ? Results.Text(contexto.User.FindFirst(CaducidadSesion.MarcaInicio)?.Value)
            : Results.Unauthorized());
        app.MapPost(CaducidadSesion.RutaSenalInteraccion, () => Results.NoContent());
        await app.StartAsync();
        using var manejador = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false };
        using var cliente = new HttpClient(manejador) { BaseAddress = new Uri(app.Urls.Single()) };

        var cookie = CookieDe(await cliente.GetAsync("/entrar"))!;

        async Task InteractuarHasta(DateTimeOffset destino)
        {
            while (reloj.Ahora < destino)
            {
                var siguiente = reloj.Ahora.AddMinutes(20);
                reloj.Ahora = siguiente < destino ? siguiente : destino;
                using var senal = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie, HttpMethod.Post);
                cookie = CookieDe(senal) ?? cookie;
            }
        }

        await InteractuarHasta(Ahora.AddHours(5));
        using (var renovacion = await Pedir(cliente, reemision == Reemision.PorSenal ? "/yo" : "/refrescar", cookie))
        {
            renovacion.StatusCode.Should().Be(HttpStatusCode.OK);
            cookie = CookieDe(renovacion) ?? cookie;
        }

        // Actividad intermedia: la reemisión con las propiedades del ticket conserva
        // también su ExpiresUtc, y solo la señal lo alarga.
        await InteractuarHasta(Ahora.AddHours(8).AddMinutes(-1));
        using (var actividad = await Pedir(cliente, "/yo", cookie))
        {
            actividad.StatusCode.Should().Be(HttpStatusCode.OK);
            cookie = CookieDe(actividad) ?? cookie;
        }

        await InteractuarHasta(Ahora.AddMinutes(719));
        using (var antes = await Pedir(cliente, "/yo", cookie))
        {
            antes.StatusCode.Should().Be(HttpStatusCode.OK);
            (await antes.Content.ReadAsStringAsync()).Should().Be(Marca(Ahora), "la hora de inicio es la del primer inicio de sesión");
            cookie = CookieDe(antes) ?? cookie;
        }

        reloj.Ahora = Ahora.AddMinutes(721);
        using var despues = await Pedir(cliente, "/yo", cookie);
        despues.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        despues.Headers.GetValues("Set-Cookie").Should().Contain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            "la cookie caducada se borra del navegador");
    }

    [Theory]
    [InlineData(719, true)]
    [InlineData(720, false)]
    [InlineData(721, false)]
    [InlineData(-1, false)]
    public void El_principal_vale_menos_de_doce_horas_desde_el_inicio(int minutos, bool vigente) =>
        CaducidadSesion.EsVigente(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CaducidadSesion.MarcaInicio, Marca(Ahora.AddMinutes(-minutos)))], "cookie")), Ahora).Should().Be(vigente);

    [Fact]
    public async Task El_circuito_caducado_falla_cerrado_sin_consultar_la_base()
    {
        // Sin SignInManager registrado: llegar a la base dejaría un aviso de error en el log.
        var registro = new RegistroDeAvisos();
        var servicios = new ServiceCollection().BuildServiceProvider();
        using var proveedor = new ProveedorAutenticacionRevalidada(new FabricaDeRegistro(registro),
            servicios.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SecurityStampValidatorOptions()), new RelojManual(Ahora));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(CaducidadSesion.MarcaInicio, Marca(Ahora.AddMinutes(-721)))], "cookie"));
        var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var resultado = (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), CancellationToken.None])!;
        (await resultado).Should().BeFalse();
        proveedor.SesionInvalidada.Should().BeTrue();
        registro.Avisos.Should().Be(0);
    }

    [Fact]
    public async Task El_circuito_sin_hora_de_inicio_falla_cerrado()
    {
        // Sin SignInManager registrado: llegar a la base dejaría un aviso de error en el log.
        var registro = new RegistroDeAvisos();
        var servicios = new ServiceCollection().BuildServiceProvider();
        using var proveedor = new ProveedorAutenticacionRevalidada(new FabricaDeRegistro(registro),
            servicios.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SecurityStampValidatorOptions()), new RelojManual(Ahora));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1")], "cookie"));
        var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        (await (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), CancellationToken.None])!).Should().BeFalse();
        proveedor.SesionInvalidada.Should().BeTrue();
        registro.Avisos.Should().Be(0);
    }

    /// <summary>
    /// Manejador de cookies real. Las peticiones corrientes, también las que reemiten la
    /// cookie deslizante pasada la mitad de su vida, no mueven el plazo; la señal sí, y
    /// deja el plazo completo por delante aunque llegue justo antes de agotarse.
    /// </summary>
    [Fact]
    public async Task Solo_la_senal_de_interaccion_mueve_el_plazo_de_inactividad()
    {
        var (app, cliente, reloj) = await ArrancarAsync();
        await using var _ = app;
        var entrada = await cliente.GetAsync("/entrar");
        entrada.Headers.GetValues("Set-Cookie").Should().ContainSingle(c => c.StartsWith(".AspNetCore.aplicacion=", StringComparison.Ordinal))
            .Which.Should().NotContain("expires", "la cookie no persistente termina al cerrar el navegador");
        var cookie = CookieDe(entrada)!;

        reloj.Ahora = Ahora.AddMinutes(29);
        using (var senal = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie, HttpMethod.Post))
        {
            senal.StatusCode.Should().Be(HttpStatusCode.OK);
            cookie = CookieDe(senal).Should().NotBeNull("la señal renueva la cookie desde el primer momento").And.Subject;
        }

        reloj.Ahora = Ahora.AddMinutes(58);
        using (var peticion = await Pedir(cliente, "/yo", cookie))
        {
            peticion.StatusCode.Should().Be(HttpStatusCode.OK, "han pasado 29 min desde la última señal");
            cookie = CookieDe(peticion) ?? cookie;
        }

        reloj.Ahora = Ahora.AddMinutes(59);
        using var caducada = await Pedir(cliente, "/yo", cookie);
        caducada.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "una petición corriente no es interacción");
        caducada.Headers.GetValues("Set-Cookie").Should().Contain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Una_senal_tardia_no_revive_la_sesion_ni_cuenta_por_get()
    {
        var (app, cliente, reloj) = await ArrancarAsync();
        await using var _ = app;
        var cookie = CookieDe(await cliente.GetAsync("/entrar"))!;

        reloj.Ahora = Ahora.AddMinutes(20);
        using (var get = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie))
        {
            get.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            cookie = CookieDe(get) ?? cookie;
        }

        reloj.Ahora = Ahora.AddMinutes(30);
        using var tardia = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie, HttpMethod.Post);
        tardia.Headers.GetValues("Set-Cookie").Should().Contain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        using var despues = await Pedir(cliente, "/yo", cookie);
        despues.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>Al desplegar, la cookie vigente aún no lleva marca de interacción: no se cierra esa sesión.</summary>
    [Fact]
    public async Task La_cookie_anterior_a_la_marca_de_interaccion_la_recibe_sin_cerrar_la_sesion()
    {
        var opciones = new CookieAuthenticationOptions();
        CaducidadSesion.Configurar(opciones, Entorno("Production"));
        var actividad = new ActividadSesiones();
        await using var servicios = new ServiceCollection().AddSingleton<TimeProvider>(new RelojManual(Ahora))
            .AddSingleton(actividad).AddSingleton<IAuthenticationService, AutenticacionFalsa>().BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = servicios };
        var propiedades = new AuthenticationProperties { IssuedUtc = Ahora.AddHours(-6), IsPersistent = true };
        propiedades.Items[CaducidadSesion.MarcaInicio] = Marca(Ahora.AddHours(-6));
        var esquema = new AuthenticationScheme("aplicacion", null, typeof(CookieAuthenticationHandler));
        var contexto = new CookieValidatePrincipalContext(http, esquema, opciones,
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([ClaimSid()], "cookie")), propiedades, esquema.Name));

        await opciones.Events.OnValidatePrincipal(contexto);

        contexto.Principal.Should().NotBeNull();
        propiedades.Items[CaducidadSesion.MarcaInteraccion].Should().Be(Marca(Ahora));
        propiedades.IsPersistent.Should().BeFalse("la cookie anterior deja de ser persistente en su siguiente reemisión");
        contexto.ShouldRenew.Should().BeFalse("en /Error no se reemite: lo guarda la siguiente revalidación del sello");
    }

    [Theory]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    [InlineData(null)]
    public async Task Una_senal_de_otro_origen_no_mueve_el_plazo(string? sitio)
    {
        var (app, cliente, reloj) = await ArrancarAsync();
        await using var _ = app;
        var cookie = CookieDe(await cliente.GetAsync("/entrar"))!;

        reloj.Ahora = Ahora.AddMinutes(20);
        using (var ajena = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie, HttpMethod.Post, sitio))
            cookie = CookieDe(ajena) ?? cookie;

        reloj.Ahora = Ahora.AddMinutes(31);
        using var despues = await Pedir(cliente, "/yo", cookie);
        despues.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Respuestas cruzadas: el último <c>Set-Cookie</c> puede ser el de una petición que salió
    /// antes de la señal. La marca anotada en el servidor impide que eso cierre la sesión.
    /// </summary>
    [Fact]
    public async Task La_cookie_con_la_marca_retrasada_vale_si_la_senal_esta_anotada()
    {
        var (app, cliente, reloj) = await ArrancarAsync();
        await using var _ = app;
        var cookieAntigua = CookieDe(await cliente.GetAsync("/entrar"))!;

        reloj.Ahora = Ahora.AddMinutes(29);
        (await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookieAntigua, HttpMethod.Post)).Dispose();

        reloj.Ahora = Ahora.AddMinutes(31);
        using var cruzada = await Pedir(cliente, "/yo", cookieAntigua);
        cruzada.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// El validador falso aplica la regla del <c>SecurityStampValidator</c>: revalida sin
    /// <c>IssuedUtc</c> o pasado el intervalo, y al revalidar reemplaza el principal por uno sin
    /// las marcas. Señales cada 50 s, menos que el intervalo, no aplazan esa revalidación, y la
    /// reemisión que sigue a una revalidación correcta conserva las marcas.
    /// </summary>
    [Fact]
    public async Task Las_senales_seguidas_no_aplazan_la_revalidacion_del_sello()
    {
        var revocada = false;
        Task Validador(CookieValidatePrincipalContext contexto)
        {
            var ahora = contexto.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
            if (contexto.Properties.IssuedUtc is { } emitida && ahora - emitida <= TimeSpan.FromSeconds(60))
                return Task.CompletedTask;
            if (revocada)
            {
                contexto.RejectPrincipal();
                return contexto.HttpContext.SignOutAsync(contexto.Scheme.Name);
            }
            contexto.ReplacePrincipal(new ClaimsPrincipal(new ClaimsIdentity(
                contexto.Principal!.Claims.Where(c => c.Type is not (CaducidadSesion.MarcaInicio or CaducidadSesion.MarcaInteraccion)), "aplicacion")));
            contexto.ShouldRenew = true;
            return Task.CompletedTask;
        }
        var (app, cliente, reloj) = await ArrancarAsync(Validador);
        await using var _ = app;
        var cookie = CookieDe(await cliente.GetAsync("/entrar"))!;

        reloj.Ahora = Ahora.AddSeconds(50);
        using (var viva = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie, HttpMethod.Post))
            cookie = CookieDe(viva).Should().NotBeNull().And.Subject;

        reloj.Ahora = Ahora.AddSeconds(70);
        using (var conMarcas = await Pedir(cliente, "/yo", cookie))
            conMarcas.StatusCode.Should().Be(HttpStatusCode.OK, "la reemisión tras revalidar conserva las marcas");

        revocada = true;
        reloj.Ahora = Ahora.AddSeconds(100);
        using var rechazada = await Pedir(cliente, CaducidadSesion.RutaSenalInteraccion, cookie, HttpMethod.Post);
        rechazada.Headers.GetValues("Set-Cookie").Should().Contain(c => c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase),
            "la señal revalida el sello aunque no haya pasado el intervalo");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-numero")]
    [InlineData("-5")]
    public async Task Una_cookie_con_marca_de_interaccion_ilegible_se_rechaza(string marca)
    {
        var opciones = new CookieAuthenticationOptions();
        CaducidadSesion.Configurar(opciones, Entorno("Production"));
        await using var servicios = new ServiceCollection().AddSingleton<TimeProvider>(new RelojManual(Ahora))
            .AddSingleton<IAuthenticationService, AutenticacionFalsa>().BuildServiceProvider();
        var propiedades = new AuthenticationProperties();
        propiedades.Items[CaducidadSesion.MarcaInicio] = Marca(Ahora.AddMinutes(-5));
        propiedades.Items[CaducidadSesion.MarcaInteraccion] = marca;
        var esquema = new AuthenticationScheme("aplicacion", null, typeof(CookieAuthenticationHandler));
        var contexto = new CookieValidatePrincipalContext(new DefaultHttpContext { RequestServices = servicios }, esquema, opciones,
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([ClaimSid()], "cookie")), propiedades, esquema.Name));

        await opciones.Events.OnValidatePrincipal(contexto);

        contexto.Principal.Should().BeNull();
    }

    [Theory]
    [InlineData(29, true)]
    [InlineData(30, false)]
    [InlineData(31, false)]
    [InlineData(-1, false)]
    public void El_principal_sigue_activo_menos_de_treinta_minutos_desde_la_interaccion(int minutos, bool activa) =>
        CaducidadSesion.SigueActiva(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CaducidadSesion.MarcaInteraccion, Marca(Ahora.AddMinutes(-minutos)))], "cookie")), null, Ahora).Should().Be(activa);

    [Fact]
    public void Sin_marca_de_interaccion_cuenta_el_inicio_de_sesion() =>
        CaducidadSesion.SigueActiva(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(CaducidadSesion.MarcaInicio, Marca(Ahora.AddMinutes(-31)))], "cookie")), null, Ahora).Should().BeFalse();

    /// <summary>
    /// El principal del circuito se queda como al conectarse. Solo la señal anotada lo
    /// mantiene vivo: el circuito abierto, por sí mismo, caduca sin consultar la base.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData(31, false)]
    [InlineData(29, null)]
    public async Task El_circuito_abierto_caduca_sin_senal_aunque_siga_conectado(int? minutosDesdeSenal, bool? vigente)
    {
        var sid = Guid.NewGuid();
        var actividad = new ActividadSesiones();
        if (minutosDesdeSenal is { } minutos)
            actividad.Anotar(sid, Ahora.AddMinutes(-minutos));
        var registro = new RegistroDeAvisos();
        var servicios = new ServiceCollection().BuildServiceProvider();
        using var proveedor = new ProveedorAutenticacionRevalidada(new FabricaDeRegistro(registro),
            servicios.GetRequiredService<IServiceScopeFactory>(), Options.Create(new SecurityStampValidatorOptions()),
            new RelojManual(Ahora), actividad);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "1"),
                new Claim(CaeManager.Infrastructure.Identity.SignInManagerCuentaDesactivada.ClaimSid, sid.ToString("N")),
                new Claim(CaducidadSesion.MarcaInicio, Marca(Ahora.AddMinutes(-90))),
                new Claim(CaducidadSesion.MarcaInteraccion, Marca(Ahora.AddMinutes(-60))),
            ], "cookie"));
        var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        if (vigente == false)
        {
            (await (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), CancellationToken.None])!).Should().BeFalse();
            proveedor.SesionInvalidada.Should().BeTrue();
            registro.Avisos.Should().Be(0);
            return;
        }

        // Con señal reciente, pasa el plazo y llega a consultar el sello: aquí, sin SignInManager
        // registrado, ese paso falla cerrado con un aviso, que es lo que distingue los dos caminos.
        (await (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), CancellationToken.None])!).Should().BeFalse();
        registro.Avisos.Should().Be(1);
    }

    [Fact]
    public void El_registro_de_actividad_retira_las_sesiones_ya_caducadas()
    {
        var actividad = new ActividadSesiones();
        var antigua = Guid.NewGuid();
        var reciente = Guid.NewGuid();
        actividad.Anotar(antigua, Ahora.AddHours(-13));
        actividad.Anotar(reciente, Ahora.AddHours(-1));

        for (var i = 0; i < 1024; i++)
            actividad.Anotar(Guid.NewGuid(), Ahora);

        actividad.Leer(antigua).Should().BeNull();
        actividad.Leer(reciente).Should().Be(Ahora.AddHours(-1));
    }

    private static async Task<(WebApplication App, HttpClient Cliente, RelojManual Reloj)> ArrancarAsync(
        Func<CookieValidatePrincipalContext, Task>? validador = null)
    {
        var reloj = new RelojManual(Ahora);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(reloj);
        var sid = ClaimSid();
        builder.Services.AddAuthentication("aplicacion").AddCookie("aplicacion");
        builder.Services.AddSingleton<ActividadSesiones>();
        builder.Services.Configure<CookieAuthenticationOptions>("aplicacion", opciones =>
        {
            if (validador is not null)
                opciones.Events.OnValidatePrincipal = validador;
            CaducidadSesion.Configurar(opciones, builder.Environment);
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/entrar", (HttpContext contexto) => contexto.SignInAsync("aplicacion",
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "1"), sid], "aplicacion")),
            new AuthenticationProperties { IsPersistent = true }));
        app.MapGet("/yo", (HttpContext contexto) => contexto.User.Identity?.IsAuthenticated == true ? Results.Ok() : Results.Unauthorized());
        app.MapPost(CaducidadSesion.RutaSenalInteraccion, () => Results.Ok());
        await app.StartAsync();
        var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        {
            BaseAddress = new Uri(app.Urls.Single())
        };
        return (app, cliente, reloj);
    }

    private static async Task<HttpResponseMessage> Pedir(
        HttpClient cliente, string ruta, string cookie, HttpMethod? metodo = null, string? sitio = "same-origin")
    {
        using var peticion = new HttpRequestMessage(metodo ?? HttpMethod.Get, ruta);
        peticion.Headers.Add("Cookie", cookie);
        if (sitio is not null)
            peticion.Headers.Add("Sec-Fetch-Site", sitio);
        return await cliente.SendAsync(peticion);
    }

    private static string? CookieDe(HttpResponseMessage respuesta) =>
        respuesta.Headers.TryGetValues("Set-Cookie", out var valores)
            ? valores.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(".AspNetCore.aplicacion=", StringComparison.Ordinal))
            : null;

    private static IHostEnvironment Entorno(string nombre) => new HostingEnvironment { EnvironmentName = nombre };
    private static string Marca(DateTimeOffset fecha) => fecha.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    private sealed class RelojManual(DateTimeOffset ahora) : TimeProvider
    {
        public DateTimeOffset Ahora { get; set; } = ahora;
        public override DateTimeOffset GetUtcNow() => Ahora;
    }

    private sealed class RegistroDeAvisos : ILogger
    {
        public int Avisos { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Avisos++;
        }
    }

    private sealed class FabricaDeRegistro(ILogger registro) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => registro;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }

    private sealed class AutenticacionFalsa : IAuthenticationService
    {
        public int Salidas { get; private set; }
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { Salidas++; return Task.CompletedTask; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => throw new NotImplementedException();
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotImplementedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotImplementedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotImplementedException();
    }
}
