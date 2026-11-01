using System.Data.Common;
using System.Globalization;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Domain.Seguridad;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Seguridad;
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
            .AddClaimsPrincipalFactory<TenantClaimsPrincipalFactory>()
            // El autenticador real: un código TOTP aceptado no vuelve a valer (V2.8.4).
            .AddTokenProvider<ProveedorTotpDeUnSoloUso>(TokenOptions.DefaultAuthenticatorProvider);
        servicios.AddScoped<IRegistroUltimoPasoTotp, RegistroUltimoPasoTotpPostgres>();
        servicios.AddSingleton(TimeProvider.System);
        // El registro de inicios de sesión tiene su propio test
        // (RegistroInicioSesionBajoRuntimeTests); aquí solo estorbaría.
        servicios.AddScoped<IVigilanciaInicioSesion, VigilanciaSinEfecto>();
        // El mismo gancho que registra InfrastructureServiceCollectionExtensions.
        servicios.Configure<SecurityStampValidatorOptions>(RestriccionLoginLocalClaimsTransformation.ConservarMetodoLoginAlRevalidar);
        servicios.Configure<SecurityStampValidatorOptions>(SignInManagerCuentaDesactivada.ConservarMarcaTotpAlRevalidar);
        servicios.Configure<SecurityStampValidatorOptions>(SignInManagerCuentaDesactivada.ConservarSidAlRevalidar);
        servicios.Configure<SecurityStampValidatorOptions>(SignInManagerCuentaDesactivada.ConservarMarcaAutenticacionAlRevalidar);
        // El registro real de sesiones: la revalidación pregunta a app_sesion_vigente.
        servicios.AddScoped<IRegistroSesionesUsuario, RegistroSesionesUsuario>();
        // La revalidación del circuito pasa por aquí; sin selección, por el SignInManager real.
        servicios.AddScoped<IVigenciaSesionCuenta, VigenciaSesionCuenta>();
        servicios.AddSingleton<IClienteActivoSeleccionado, SinSeleccion>();
        // El hash ficticio del login con cuenta inexistente (S-A-10); aquí no se mide su coste.
        servicios.AddSingleton(new VerificacionContrasenaFicticia(new PasswordHasher<ApplicationUser>()));

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
    public async Task El_validador_real_de_la_cookie_conserva_la_marca_de_Microsoft_al_rehacer_el_principal()
    {
        // La fábrica de claims no conoce la marca que el callback de Microsoft añade al iniciar sesión;
        // sin el gancho, la primera revalidación la borraba y la sesión caía en Consulta.
        var usuario = await CrearUsuarioAsync("marca-microsoft@x.test");
        var conMarca = await PrincipalDeSesionAsync(usuario);
        ((ClaimsIdentity)conMarca.Identity!).AddClaim(new Claim(
            RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso));
        var sinMarca = await PrincipalDeSesionAsync(usuario);

        var rehecho = await PrincipalTrasElValidadorDeLaCookieAsync(conMarca);
        var rehechoSinMarca = await PrincipalTrasElValidadorDeLaCookieAsync(sinMarca);

        rehecho.Should().NotBeNull();
        rehecho.Should().NotBeSameAs(conMarca, "control: el validador rehízo el principal con la fábrica");
        rehecho!.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin).Select(c => c.Value)
            .Should().Equal([RestriccionLoginLocalClaimsTransformation.MetodoLoginSso]);
        rehechoSinMarca.Should().NotBeNull();
        rehechoSinMarca!.HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin)
            .Should().BeFalse("la marca nunca se crea donde no estaba");

        // De punta a punta: fábrica real (tenant_id de la cuenta) → validador → restricción, con el Tenant de
        // la cuenta como Tenant declarado. Por Microsoft sigue escribiendo; con contraseña, Consulta.
        var restriccion = new RestriccionLoginLocalClaimsTransformation(Options.Create(new AzureAdOptions
        {
            TenantId = "tenant-ficticio",
            ClientId = "aplicacion-ficticia",
            ClientSecret = "valor-ficticio",
            TenantHydraId = _tenant,
        }));
        rehecho.FindFirst(TenantClaimsPrincipalFactory.TipoClaimTenantId)!.Value.Should().Be(_tenant.ToString());
        (await restriccion.TransformAsync(rehecho)).FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.GestorCae]);
        (await restriccion.TransformAsync(rehechoSinMarca)).FindAll(ClaimTypes.Role).Select(c => c.Value).Should().Equal([Roles.Consulta]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refrescar_la_sesion_conserva_la_marca_de_Microsoft_y_no_la_inventa(bool conMarca)
    {
        // Cambiar la contraseña o activar el segundo factor llaman a RefreshSignInAsync.
        var usuario = await CrearUsuarioAsync($"refresco-{conMarca}@x.test");
        var cookie = await EmitirCookieAsync(usuario.Id, refrescar: false, cookiePrevia: null, conMarcaSso: conMarca);
        PrincipalDeLaCookie(cookie)!.HasClaim(c => c.Type == RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin)
            .Should().Be(conMarca, "control: la cookie de partida");

        var refrescada = await EmitirCookieAsync(usuario.Id, refrescar: true, cookiePrevia: cookie);

        refrescada.Should().NotBeEmpty("control: el refresco emitió una cookie nueva");
        string[] esperado = conMarca ? [RestriccionLoginLocalClaimsTransformation.MetodoLoginSso] : [];
        PrincipalDeLaCookie(refrescada)!.FindAll(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin)
            .Select(c => c.Value).Should().Equal(esperado);
    }

    [Fact]
    public async Task Refrescar_la_sesion_conserva_el_instante_del_TOTP_sin_rejuvenecerlo()
    {
        // S4: la apertura de una Sesión Privilegiada exige un TOTP reciente; cambiar la
        // contraseña (RefreshSignInAsync) no puede convertir uno viejo en uno nuevo.
        var usuario = await CrearUsuarioAsync("refresco-totp@x.test");
        var hace3Horas = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var cookie = CookieConMarcaTotp(await PrincipalDeSesionAsync(usuario), hace3Horas);
        PrincipalDeLaCookie(cookie)!.FindFirst(SignInManagerCuentaDesactivada.ClaimTotpSesion)!.Value
            .Should().Be(hace3Horas, "control: la cookie de partida");

        var refrescada = await EmitirCookieAsync(usuario.Id, refrescar: true, cookiePrevia: cookie);

        refrescada.Should().NotBeEmpty("control: el refresco emitió una cookie nueva");
        PrincipalDeLaCookie(refrescada)!.FindAll(SignInManagerCuentaDesactivada.ClaimTotpSesion)
            .Select(c => c.Value).Should().Equal([hace3Horas]);
    }

    [Fact]
    public async Task La_marca_del_TOTP_no_entra_por_claims_adicionales_ni_se_inventa_al_refrescar()
    {
        var usuario = await CrearUsuarioAsync("marca-totp-ajena@x.test");
        var ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        string emitida;
        using (var ambito = _servicios.CreateScope())
        {
            var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
            ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
            var cuenta = await ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuario.Id.ToString());
            await ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>()
                .SignInWithClaimsAsync(cuenta!, isPersistent: false, [new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion, ahora)]);
            emitida = contexto.Response.Headers.SetCookie.ToString();
        }

        emitida.Should().NotBeEmpty("control: se emitió la cookie");
        PrincipalDeLaCookie(emitida)!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();

        var refrescada = await EmitirCookieAsync(usuario.Id, refrescar: true, cookiePrevia: emitida);
        refrescada.Should().NotBeEmpty("control: el refresco emitió una cookie nueva");
        PrincipalDeLaCookie(refrescada)!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Una_sesion_nueva_lleva_su_marca_de_inicio_y_el_refresco_la_conserva_sin_rejuvenecerla()
    {
        // ASVS 4.0.3 V3.7.1: el alta del TOTP lee esta marca como autenticación reciente. La pone
        // la emisión de una sesión nueva, nunca un claim adicional, y refrescar la cookie no la mueve.
        var usuario = await CrearUsuarioAsync("marca-inicio@x.test");

        string emitida;
        using (var ambito = _servicios.CreateScope())
        {
            var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
            ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
            var cuenta = await ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuario.Id.ToString());
            await ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>()
                .SignInWithClaimsAsync(cuenta!, isPersistent: false, [new Claim(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion, "1")]);
            emitida = contexto.Response.Headers.SetCookie.ToString();
        }

        emitida.Should().NotBeEmpty("control: se emitió la cookie");
        var marca = PrincipalDeLaCookie(emitida)!.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion)
            .Select(c => c.Value).ToList();
        marca.Should().ContainSingle().Which.Should().NotBe("1", "la marca que llega como claim adicional se descarta");
        SignInManagerCuentaDesactivada.AutenticacionEsReciente(PrincipalDeLaCookie(emitida), DateTimeOffset.UtcNow).Should().BeTrue();

        var refrescada = await EmitirCookieAsync(usuario.Id, refrescar: true, cookiePrevia: emitida);
        refrescada.Should().NotBeEmpty("control: el refresco emitió una cookie nueva");
        PrincipalDeLaCookie(refrescada)!.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion)
            .Select(c => c.Value).Should().Equal(marca);
    }

    [Fact]
    public async Task Revalidar_la_cookie_conserva_la_marca_del_TOTP_con_su_instante()
    {
        // El SecurityStampValidator real rehace el principal con la fábrica de claims al vencer
        // el intervalo de revalidación: sin ConservarMarcaTotpAlRevalidar la marca moría ahí.
        const string Centinela = "prueba:centinela";
        var usuario = await CrearUsuarioAsync("revalidar-totp@x.test");
        var hace3Horas = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var principal = await PrincipalDeSesionAsync(usuario);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(Centinela, "de-la-cookie"));
        var cookie = CookieConMarcaTotp(principal, hace3Horas, emitidaEnUtc: DateTimeOffset.UtcNow.AddHours(-2));

        using var ambito = _servicios.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        contexto.Request.Headers.Cookie = cookie;
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
        var resultado = await contexto.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        resultado.Succeeded.Should().BeTrue("control: la cookie de una cuenta activa sigue validando");
        resultado.Principal!.HasClaim(c => c.Type == Centinela)
            .Should().BeFalse("control: el validador rehízo el principal con la fábrica de claims");
        resultado.Principal!.FindAll(SignInManagerCuentaDesactivada.ClaimTotpSesion)
            .Select(c => c.Value).Should().Equal([hace3Horas]);
    }

    /// <summary>
    /// Solo acertar el TOTP pone la marca que exige abrir una Sesión Privilegiada; entrar con un código
    /// de recuperación, con el mismo segundo paso pendiente, no la pone.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solo_el_TOTP_acertado_pone_la_marca_de_la_sesion(bool conTotp)
    {
        var usuario = await CrearUsuarioAsync($"segundo-paso-{conTotp}@x.test");
        string clave;
        string codigoRecuperacion;
        using (var preparacion = _servicios.CreateScope())
        {
            var userManager = preparacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
            (await userManager.ResetAuthenticatorKeyAsync(cuenta)).Succeeded.Should().BeTrue();
            clave = (await userManager.GetAuthenticatorKeyAsync(cuenta))!;
            (await userManager.SetTwoFactorEnabledAsync(cuenta, true)).Succeeded.Should().BeTrue();
            codigoRecuperacion = (await userManager.GenerateNewTwoFactorRecoveryCodesAsync(cuenta, 1))!.Single();
        }

        using var ambito = _servicios.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        contexto.Request.Headers.Cookie = CookieDeSegundoPasoPendiente(usuario.Id);
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
        var signInManager = ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();

        var resultado = conTotp
            ? await signInManager.TwoFactorAuthenticatorSignInAsync(CodigoTotp(clave), isPersistent: false, rememberClient: false)
            : await signInManager.TwoFactorRecoveryCodeSignInAsync(codigoRecuperacion);

        resultado.Succeeded.Should().BeTrue("control: el segundo paso se completó y se emitió la sesión");
        var emitida = PrincipalDeLaCookie(CookieDeAplicacionEmitida(contexto))!;
        if (conTotp)
            SignInManagerCuentaDesactivada.TotpEsReciente(emitida, DateTimeOffset.UtcNow).Should().BeTrue();
        else
            emitida.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();
    }

    /// <summary>
    /// ASVS V2.8.4 en el segundo paso del login: el TOTP que ya completó un inicio de sesión no completa
    /// otro desde una petición nueva, y la repetición cuenta como intento fallido en el bloqueo de la cuenta.
    /// </summary>
    [Fact]
    public async Task El_mismo_TOTP_no_completa_dos_veces_el_segundo_paso()
    {
        var usuario = await CrearUsuarioAsync("totp-repetido@x.test");
        string clave;
        using (var preparacion = _servicios.CreateScope())
        {
            var userManager = preparacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
            (await userManager.ResetAuthenticatorKeyAsync(cuenta)).Succeeded.Should().BeTrue();
            clave = (await userManager.GetAuthenticatorKeyAsync(cuenta))!;
            (await userManager.SetTwoFactorEnabledAsync(cuenta, true)).Succeeded.Should().BeTrue();
        }
        var codigo = CodigoTotp(clave);

        (await SegundoPasoConTotpAsync(usuario.Id, codigo)).Succeeded.Should().BeTrue(
            "control positivo: el código del paso actual completa el segundo paso");
        (await SegundoPasoConTotpAsync(usuario.Id, codigo)).Succeeded.Should().BeFalse(
            "un código aceptado no vuelve a valer dentro de su ventana");

        using var lectura = _servicios.CreateScope();
        (await lectura.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuario.Id.ToString()))!
            .AccessFailedCount.Should().Be(1, "repetir el código cuenta como intento fallido");
    }

    private async Task<SignInResult> SegundoPasoConTotpAsync(Guid usuarioId, string codigo)
    {
        using var ambito = _servicios.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        contexto.Request.Headers.Cookie = CookieDeSegundoPasoPendiente(usuarioId);
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
        return await ambito.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>()
            .TwoFactorAuthenticatorSignInAsync(codigo, isPersistent: false, rememberClient: false);
    }

    /// <summary>La cookie del segundo paso pendiente que deja la contraseña acertada («nombre=valor»).</summary>
    private string CookieDeSegundoPasoPendiente(Guid usuarioId)
    {
        var opciones = _servicios.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.TwoFactorUserIdScheme);
        var identidad = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identidad.AddClaim(new Claim(ClaimTypes.Name, usuarioId.ToString()));
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identidad), new AuthenticationProperties(), IdentityConstants.TwoFactorUserIdScheme);
        return $"{opciones.Cookie.Name}={opciones.TicketDataFormat.Protect(ticket)}";
    }

    /// <summary>De las Set-Cookie de la respuesta, la de la cookie de aplicación (la otra borra la del segundo paso).</summary>
    private string CookieDeAplicacionEmitida(HttpContext contexto)
    {
        var nombre = _servicios.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).Cookie.Name + "=";
        return contexto.Response.Headers.SetCookie.Single(c => c!.StartsWith(nombre, StringComparison.Ordinal))!;
    }

    /// <summary>TOTP de RFC 6238 (SHA-1, 30 s, 6 dígitos) sobre la clave en Base32 que da Identity.</summary>
    private static string CodigoTotp(string claveBase32)
    {
        const string Alfabeto = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(claveBase32.TrimEnd('=').ToUpperInvariant()
            .Select(c => Convert.ToString(Alfabeto.IndexOf(c), 2).PadLeft(5, '0')));
        var clave = Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();

        var paso = BitConverter.GetBytes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        if (BitConverter.IsLittleEndian) Array.Reverse(paso);
        var hash = System.Security.Cryptography.HMACSHA1.HashData(clave, paso);
        var desplazamiento = hash[^1] & 0x0F;
        var valor = ((hash[desplazamiento] & 0x7F) << 24) | (hash[desplazamiento + 1] << 16)
            | (hash[desplazamiento + 2] << 8) | hash[desplazamiento + 3];
        return (valor % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Una_marca_del_TOTP_guardada_en_la_cuenta_no_llega_a_la_sesion()
    {
        // La fábrica copia los claims de AspNetUserClaims: uno con la marca no cuenta como TOTP,
        // ni al iniciar sesión ni para quien llame a la fábrica directamente.
        var usuario = await CrearUsuarioAsync("marca-guardada@x.test");
        var ahora = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using (var preparacion = _servicios.CreateScope())
        {
            var userManager = preparacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
            (await userManager.AddClaimAsync(cuenta, new Claim(SignInManagerCuentaDesactivada.ClaimTotpSesion, ahora))).Succeeded.Should().BeTrue();
            (await userManager.GetClaimsAsync(cuenta)).Should().Contain(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion,
                "control: el claim quedó guardado en la cuenta");
        }

        var cookie = await EmitirCookieAsync(usuario.Id, refrescar: false, cookiePrevia: null);
        cookie.Should().NotBeEmpty("control: se emitió la cookie");
        PrincipalDeLaCookie(cookie)!.HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();

        using var ambito = _servicios.CreateScope();
        var fabrica = ambito.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var cuentaLeida = (await ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuario.Id.ToString()))!;
        (await fabrica.CreateAsync(cuentaLeida)).HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimTotpSesion).Should().BeFalse();
    }

    [Fact]
    public async Task Un_identificador_de_sesion_guardado_en_la_cuenta_no_llega_a_la_sesion()
    {
        // Con un talveg:sid en AspNetUserClaims, todas las sesiones de la cuenta compartirían uno:
        // la fábrica lo descarta y la cookie lleva solo el que registra el inicio de sesión.
        var usuario = await CrearUsuarioAsync("sid-guardado@x.test");
        var guardado = Guid.NewGuid();
        using (var preparacion = _servicios.CreateScope())
        {
            var userManager = preparacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
            (await userManager.AddClaimAsync(cuenta, new Claim(SignInManagerCuentaDesactivada.ClaimSid, guardado.ToString("N")))).Succeeded.Should().BeTrue();
            (await userManager.GetClaimsAsync(cuenta)).Should().Contain(c => c.Type == SignInManagerCuentaDesactivada.ClaimSid,
                "control: el claim quedó guardado en la cuenta");
        }

        var cookie = await EmitirCookieAsync(usuario.Id, refrescar: false, cookiePrevia: null);
        cookie.Should().NotBeEmpty("control: se emitió la cookie");
        var principal = PrincipalDeLaCookie(cookie)!;
        principal.FindAll(SignInManagerCuentaDesactivada.ClaimSid).Should().ContainSingle();
        SignInManagerCuentaDesactivada.LeerSid(principal).Should().NotBe(guardado);

        using var ambito = _servicios.CreateScope();
        var fabrica = ambito.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var cuentaLeida = (await ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuario.Id.ToString()))!;
        (await fabrica.CreateAsync(cuentaLeida)).HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimSid).Should().BeFalse();
    }

    [Fact]
    public async Task Refrescar_la_sesion_no_rejuvenece_un_inicio_de_sesion_antiguo()
    {
        // ASVS 4.0.3 V3.7.1: cambiar la contraseña (RefreshSignInAsync) no puede convertir un inicio
        // de sesión de hace horas en uno reciente que abra el alta del TOTP sin pedir la contraseña.
        var usuario = await CrearUsuarioAsync("refresco-inicio@x.test");
        var hace3Horas = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var cookie = CookieConMarca(
            await PrincipalDeSesionAsync(usuario), SignInManagerCuentaDesactivada.ClaimAutenticacionSesion, hace3Horas);
        SignInManagerCuentaDesactivada.AutenticacionEsReciente(PrincipalDeLaCookie(cookie), DateTimeOffset.UtcNow)
            .Should().BeFalse("control: la cookie de partida no es reciente");

        var refrescada = await EmitirCookieAsync(usuario.Id, refrescar: true, cookiePrevia: cookie);

        refrescada.Should().NotBeEmpty("control: el refresco emitió una cookie nueva");
        var principal = PrincipalDeLaCookie(refrescada)!;
        principal.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion).Select(c => c.Value).Should().Equal([hace3Horas]);
        SignInManagerCuentaDesactivada.AutenticacionEsReciente(principal, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public async Task Revalidar_la_cookie_conserva_el_inicio_de_sesion_con_su_instante()
    {
        // El SecurityStampValidator real rehace el principal con la fábrica de claims: sin
        // ConservarMarcaAutenticacionAlRevalidar la marca moriría y el alta pediría la contraseña
        // en mitad del proceso; con un valor nuevo, se rejuvenecería.
        const string Centinela = "prueba:centinela";
        var usuario = await CrearUsuarioAsync("revalidar-inicio@x.test");
        var hace3Horas = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var principal = await PrincipalDeSesionAsync(usuario);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(Centinela, "de-la-cookie"));
        var cookie = CookieConMarca(
            principal, SignInManagerCuentaDesactivada.ClaimAutenticacionSesion, hace3Horas, emitidaEnUtc: DateTimeOffset.UtcNow.AddHours(-2));

        using var ambito = _servicios.CreateScope();
        var contexto = new DefaultHttpContext { RequestServices = ambito.ServiceProvider };
        contexto.Request.Headers.Cookie = cookie;
        ambito.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = contexto;
        var resultado = await contexto.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        resultado.Succeeded.Should().BeTrue("control: la cookie de una cuenta activa sigue validando");
        resultado.Principal!.HasClaim(c => c.Type == Centinela)
            .Should().BeFalse("control: el validador rehízo el principal con la fábrica de claims");
        resultado.Principal!.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion)
            .Select(c => c.Value).Should().Equal([hace3Horas]);
    }

    [Fact]
    public async Task Un_inicio_de_sesion_guardado_en_la_cuenta_no_llega_a_la_sesion()
    {
        // Con la marca en AspNetUserClaims, toda sesión de la cuenta parecería recién iniciada para
        // siempre: la fábrica la descarta y la cookie lleva solo la que pone el inicio de sesión.
        var usuario = await CrearUsuarioAsync("inicio-guardado@x.test");
        var futuro = DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using (var preparacion = _servicios.CreateScope())
        {
            var userManager = preparacion.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var cuenta = (await userManager.FindByIdAsync(usuario.Id.ToString()))!;
            (await userManager.AddClaimAsync(cuenta, new Claim(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion, futuro))).Succeeded.Should().BeTrue();
            (await userManager.GetClaimsAsync(cuenta)).Should().Contain(c => c.Type == SignInManagerCuentaDesactivada.ClaimAutenticacionSesion,
                "control: el claim quedó guardado en la cuenta");
        }

        var cookie = await EmitirCookieAsync(usuario.Id, refrescar: false, cookiePrevia: null);
        cookie.Should().NotBeEmpty("control: se emitió la cookie");
        PrincipalDeLaCookie(cookie)!.FindAll(SignInManagerCuentaDesactivada.ClaimAutenticacionSesion)
            .Should().ContainSingle().Which.Value.Should().NotBe(futuro);

        using var ambito = _servicios.CreateScope();
        var fabrica = ambito.ServiceProvider.GetRequiredService<IUserClaimsPrincipalFactory<ApplicationUser>>();
        var cuentaLeida = (await ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByIdAsync(usuario.Id.ToString()))!;
        (await fabrica.CreateAsync(cuentaLeida)).HasClaim(c => c.Type == SignInManagerCuentaDesactivada.ClaimAutenticacionSesion).Should().BeFalse();
    }

    /// <summary>Cookie de aplicación («nombre=valor») con la marca de TOTP que solo pone el segundo factor.</summary>
    private string CookieConMarcaTotp(ClaimsPrincipal principal, string marca, DateTimeOffset? emitidaEnUtc = null) =>
        CookieConMarca(principal, SignInManagerCuentaDesactivada.ClaimTotpSesion, marca, emitidaEnUtc);

    /// <summary>Cookie de aplicación («nombre=valor») con una marca de sesión que solo pone el servidor.</summary>
    private string CookieConMarca(ClaimsPrincipal principal, string tipo, string marca, DateTimeOffset? emitidaEnUtc = null)
    {
        var opciones = _servicios.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(tipo, marca));
        var ticket = new AuthenticationTicket(
            principal, new AuthenticationProperties { IssuedUtc = emitidaEnUtc ?? DateTimeOffset.UtcNow }, IdentityConstants.ApplicationScheme);
        return $"{opciones.Cookie.Name}={opciones.TicketDataFormat.Protect(ticket)}";
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
            IntervaloDeUnSegundo);
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
    public async Task El_circuito_abierto_pasa_a_anonimo_cuando_el_Tenant_exige_un_segundo_factor_que_no_tiene()
    {
        var usuario = await CrearUsuarioAsync("circuito-exigido@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        var proveedor = new ProveedorAutenticacionRevalidada(
            _servicios.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            _servicios.GetRequiredService<IServiceScopeFactory>(),
            IntervaloDeUnSegundo);
        try
        {
            proveedor.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));

            // Control positivo: sin la exigencia, el bucle no expulsa a nadie.
            await Task.Delay(TimeSpan.FromSeconds(2.5));
            proveedor.SesionInvalidada.Should().BeFalse();

            using (var ambito = _servicios.CreateScope())
            using (AmbitoTenantExplicito.Establecer(_tenant))
            {
                var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                var parametros = new CaeManager.Domain.Configuracion.ParametroSistema(30, 15);
                parametros.ActualizarSeguridadAcceso(30, exigeSegundoFactorTodosLosPerfiles: true);
                contexto.ParametrosSistema.Add(parametros);
                // Este contexto no lleva TenantSelladoInterceptor: sin sellar a mano, la fila quedaría
                // con TenantId vacío, la fábrica no la vería y regirían los valores por defecto.
                contexto.Entry(parametros).Property(p => p.TenantId).CurrentValue = _tenant;
                await contexto.SaveChangesAsync();
            }

            var limite = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < limite && !proveedor.SesionInvalidada)
                await Task.Delay(200);

            proveedor.SesionInvalidada.Should().BeTrue(
                "por SignalR no actúa el middleware: la exigencia solo se aplica obligando a recargar");
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    [Fact]
    public async Task Una_cancelacion_que_no_es_del_ciclo_invalida_el_circuito()
    {
        // Un tiempo de espera del proveedor de base de datos llega como
        // OperationCanceledException con un token que NO es el del ciclo. No es
        // una cancelación del circuito sino un error al revalidar: tras repetir
        // la consulta una vez, falla cerrado y el circuito pasa a anónimo.
        var usuario = await CrearUsuarioAsync("cancelacion-ajena@x.test");
        var principal = await PrincipalDeSesionAsync(usuario);

        var proveedor = new ProveedorAutenticacionRevalidada(
            _servicios.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>(),
            new AmbitosQueSeCancelan(),
            IntervaloDeUnSegundo);
        try
        {
            proveedor.SetAuthenticationState(Task.FromResult(new AuthenticationState(principal)));

            await Task.Delay(TimeSpan.FromSeconds(3.5)); // al menos un ciclo completo con la cancelación ajena
            (await proveedor.GetAuthenticationStateAsync()).User.Identity!.IsAuthenticated.Should().BeFalse(
                "un timeout ajeno al ciclo invalida la sesión");
            proveedor.SesionInvalidada.Should().BeTrue();
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
            IntervaloDeUnSegundo);
        try
        {
            using var ciclo = new CancellationTokenSource();
            ciclo.Cancel();

            var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod(
                "ValidateAuthenticationStateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var tarea = (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), ciclo.Token])!;

            var excepcion = await tarea.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
            excepcion.Which.Should().BeOfType<TaskCanceledException>(
                "RevalidationLoop solo trata como cierre normal una TaskCanceledException; otra cosa cae en ForceSignOut");
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

        // Presupuesto, no una medida exacta: validar lee el usuario y pregunta
        // si su sesión sigue sin revocar (app_sesion_vigente); reconstruir el
        // principal añade roles, claims y el ajuste del Tenant. El circuito y
        // la cookie hacen ambas cosas en cada ciclo. Si esto crece sin querer,
        // el coste por sesión activa y minuto deja de ser el medido.
        soloValidar.Should().BeLessThanOrEqualTo(2);
        conRenovacionDeCookie.Should().BeLessThanOrEqualTo(8);
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
        var principal = await signInManager.CreateUserPrincipalAsync(usuario);
        // Como el de la cookie: la sesión lleva su hora de inicio (CaducidadSesion);
        // sin ella el circuito falla cerrado antes de preguntar a la base.
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(CaeManager.Web.Services.CaducidadSesion.MarcaInicio,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Y su identificador de sesión, registrado como lo registra el inicio de sesión.
        var sid = Guid.NewGuid();
        await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().RegistrarAsync(usuario.Id, usuario.TenantId, sid);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(SignInManagerCuentaDesactivada.ClaimSid, sid.ToString("N")));
        return principal;
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
    private async Task<bool> ValidarConElValidadorDeLaCookieAsync(ClaimsPrincipal principal) =>
        await PrincipalTrasElValidadorDeLaCookieAsync(principal) is not null;

    /// <summary>El principal que deja el validador real de la cookie vencida, o <c>null</c> si la rechaza.</summary>
    private async Task<ClaimsPrincipal?> PrincipalTrasElValidadorDeLaCookieAsync(ClaimsPrincipal principal)
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

        return contexto.Principal;
    }

    /// <summary>Devuelve el valor de la cabecera Set-Cookie que produce RefreshSignInAsync (o SignInAsync), o vacío si no emitió nada.</summary>
    /// <summary>El principal que lleva una cookie de aplicación emitida (valor de Set-Cookie), o <c>null</c>.</summary>
    private ClaimsPrincipal? PrincipalDeLaCookie(string setCookie)
    {
        var opciones = _servicios.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        var par = setCookie.Split(';')[0];
        return opciones.TicketDataFormat.Unprotect(Uri.UnescapeDataString(par[(par.IndexOf('=') + 1)..]))?.Principal;
    }

    private async Task<string> EmitirCookieAsync(Guid usuarioId, bool refrescar, string? cookiePrevia, bool conMarcaSso = false)
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
        else if (conMarcaSso)
            // Como el callback de Microsoft (IdentityEndpointsExtensions).
            await signInManager.SignInWithClaimsAsync(usuario!, isPersistent: false, [new Claim(
                RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso)]);
        else
            await signInManager.SignInAsync(usuario!, isPersistent: false);

        return contexto.Response.Headers.SetCookie.ToString();
    }

    /// <summary>
    /// Solo lo usan los proveedores de circuito, para que el bucle dé varios ciclos en
    /// segundos. NO va en el contenedor: con un intervalo de 1 s, el validador real de la
    /// cookie corre en cualquier <c>AuthenticateAsync</c> que tarde más de un segundo y
    /// rechaza a la cuenta desactivada antes de llegar a <c>RefreshSignInAsync</c> (emite
    /// una cookie de borrado, no una nueva), lo que volvía intermitente la prueba del
    /// bypass y, peor, la dejaba sin ejercitar el freno de emisión cuando ocurría.
    /// </summary>
    private static readonly IOptions<SecurityStampValidatorOptions> IntervaloDeUnSegundo =
        Options.Create(new SecurityStampValidatorOptions { ValidationInterval = TimeSpan.FromSeconds(1) });

    private sealed class AmbitosQueSeCancelan : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new TaskCanceledException("tiempo de espera del proveedor");
    }

    // Consulta el ámbito explícito igual que TenantActual: la fábrica de claims lee los parámetros
    // del Tenant de la cuenta dentro de ese ámbito, y con un valor fijo el filtro global no vería la fila.
    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
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

    private sealed class VigilanciaSinEfecto : IVigilanciaInicioSesion
    {
        public Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default) => Task.FromResult(true);

        public Task RegistrarAsync(
            Guid? usuarioId, Guid? tenantId, MetodoInicioSesion metodo, ResultadoInicioSesion resultado,
            SenalesInicioSesion senalesConocidas = SenalesInicioSesion.Ninguna, CancellationToken ct = default) => Task.CompletedTask;

        public Task<ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(string usuarioId, string codigo, CancellationToken ct = default)
            => Task.FromResult(ResultadoNoHeSidoYo.EnlaceNoValido);
    }

    private sealed class SinSeleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
