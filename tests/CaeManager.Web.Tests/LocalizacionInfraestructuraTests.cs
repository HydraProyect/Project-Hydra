using System.Globalization;
using System.Security.Claims;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using CaeManager.Web.Recursos;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Opciones = Microsoft.Extensions.Options.Options;

namespace CaeManager.Web.Tests;

/// <summary>
/// Infraestructura de idioma por cuenta (es-ES por defecto, ca-ES soportado):
/// resolución de la cultura de cada petición con las opciones reales de la
/// aplicación (<see cref="CulturaUsuarioCookie.ConfigurarLocalizacion"/>, las
/// mismas que registra Program.cs) sobre el <see cref="RequestLocalizationMiddleware"/>
/// real; la cookie que proyecta <c>ApplicationUser.Idioma</c>; el manejador de
/// <c>POST /cuenta/idioma</c> y su invariante «la cookie nunca se adelanta a
/// la cuenta»; y la resolución de recursos neutral/ca-ES.
///
/// <para>
/// Límite del instrumento: aquí no hay transporte HTTP ni navegador. Que los
/// inicios de sesión reales emitan el <c>Set-Cookie</c> y que el siguiente
/// GET salga en esa cultura lo prueban los E2E (<c>IdiomaPorCuentaTests</c>).
/// </para>
/// </summary>
public class LocalizacionInfraestructuraTests
{
    // ── Resolución de la cultura por petición ─────────────────────────────

    [Fact]
    public async Task Sin_cookie_la_peticion_sale_en_es_ES()
    {
        var (cultura, culturaUi, _) = await ResolverAsync(_ => { });

        cultura.Should().Be("es-ES");
        culturaUi.Should().Be("es-ES");
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public async Task La_cookie_de_cultura_decide_la_cultura_de_la_peticion(string culturaCookie)
    {
        var (cultura, culturaUi, contentLanguage) = await ResolverAsync(peticion =>
            peticion.Headers.Cookie = CabeceraCookie(culturaCookie));

        cultura.Should().Be(culturaCookie);
        culturaUi.Should().Be(culturaCookie);
        contentLanguage.Should().Be(culturaCookie, "Content-Language publica la cultura activa");
    }

    [Fact]
    public async Task Accept_Language_no_sustituye_la_preferencia_de_la_cuenta()
    {
        var (_, culturaUi, _) = await ResolverAsync(peticion =>
            peticion.Headers.AcceptLanguage = "ca-ES,ca;q=0.9");

        culturaUi.Should().Be("es-ES");
    }

    [Fact]
    public async Task La_query_string_no_cambia_la_cultura()
    {
        var (_, culturaUi, _) = await ResolverAsync(peticion =>
            peticion.QueryString = new QueryString("?culture=ca-ES&ui-culture=ca-ES"));

        culturaUi.Should().Be("es-ES");
    }

    [Fact]
    public async Task Una_cultura_no_soportada_en_la_cookie_cae_a_es_ES()
    {
        var (_, culturaUi, _) = await ResolverAsync(peticion =>
            peticion.Headers.Cookie = CabeceraCookie("en-US"));

        culturaUi.Should().Be("es-ES");
    }

    /// <summary>
    /// Program.cs conserva <c>DefaultThreadCurrentCulture = es-ES</c> como
    /// fallback de la ejecución fuera de una petición: no debe impedir que una
    /// petición con cookie ca-ES se ejecute en ca-ES.
    /// </summary>
    [Fact]
    public async Task El_default_global_es_ES_no_impide_que_una_peticion_ca_ES_se_ejecute_en_ca_ES()
    {
        var culturaPrevia = CultureInfo.DefaultThreadCurrentCulture;
        var culturaUiPrevia = CultureInfo.DefaultThreadCurrentUICulture;
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("es-ES");
            CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("es-ES");

            var (cultura, culturaUi, _) = await ResolverAsync(peticion =>
                peticion.Headers.Cookie = CabeceraCookie("ca-ES"));

            cultura.Should().Be("ca-ES");
            culturaUi.Should().Be("ca-ES");
        }
        finally
        {
            CultureInfo.DefaultThreadCurrentCulture = culturaPrevia;
            CultureInfo.DefaultThreadCurrentUICulture = culturaUiPrevia;
        }
    }

    // ── Cookie de cultura ─────────────────────────────────────────────────

    [Theory]
    [InlineData(IdiomaPreferido.Espanol, "es-ES")]
    [InlineData(IdiomaPreferido.Catalan, "ca-ES")]
    public void La_correspondencia_idioma_cultura_es_de_ida_y_vuelta(IdiomaPreferido idioma, string cultura)
    {
        CulturaUsuarioCookie.ACultura(idioma).Should().Be(cultura);
        CulturaUsuarioCookie.IntentarDesdeCultura(cultura, out var deVuelta).Should().BeTrue();
        deVuelta.Should().Be(idioma);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ca")]
    [InlineData("ca-es")]
    [InlineData("en-US")]
    [InlineData("fr-FR")]
    public void Solo_las_culturas_soportadas_pasan_la_lista_blanca(string? cultura) =>
        CulturaUsuarioCookie.IntentarDesdeCultura(cultura, out _).Should().BeFalse();

    [Fact]
    public void Escribir_emite_una_cookie_persistente_con_Path_raiz_HttpOnly_y_Lax()
    {
        var contexto = new DefaultHttpContext();

        CulturaUsuarioCookie.Escribir(contexto, IdiomaPreferido.Catalan);

        var setCookie = UnicoSetCookie(contexto);
        setCookie.Should().StartWith(".AspNetCore.Culture=c%3Dca-ES%7Cuic%3Dca-ES;");
        setCookie.Should().Contain("path=/");
        setCookie.Should().Contain("httponly");
        setCookie.Should().Contain("samesite=lax");

        // Persistente: con fecha de caducidad a un año vista, no de sesión.
        var caducidad = ExtraerCaducidad(setCookie);
        caducidad.Should().NotBeNull("una cookie sin expires es de sesión y se pierde al cerrar el navegador");
        caducidad!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(365), TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Eliminar_expira_la_cookie_con_el_mismo_Path_con_el_que_se_escribio()
    {
        var contexto = new DefaultHttpContext();

        CulturaUsuarioCookie.Eliminar(contexto);

        var setCookie = UnicoSetCookie(contexto);
        setCookie.Should().StartWith(".AspNetCore.Culture=;");
        setCookie.Should().Contain("path=/");
        ExtraerCaducidad(setCookie).Should().BeBefore(DateTimeOffset.UtcNow);
    }

    // ── POST /cuenta/idioma ───────────────────────────────────────────────

    [Fact]
    public async Task Cambiar_persiste_el_idioma_y_solo_entonces_escribe_la_cookie()
    {
        var usuario = NuevoUsuario();
        var usuarios = new UsuariosFalsos(usuario);
        var contexto = ContextoAutenticado(usuario.Id);

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "ca-ES", "/documentos?pestana=pendientes", contexto, usuarios, new DesenganchadorFalso(), Textos(), NullLogger.Instance);

        usuarios.Guardados.Should().ContainSingle().Which.Should().Be(IdiomaPreferido.Catalan);
        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/documentos?pestana=pendientes");
        UnicoSetCookie(contexto).Should().StartWith(".AspNetCore.Culture=c%3Dca-ES%7Cuic%3Dca-ES;");
    }

    [Fact]
    public async Task Un_returnUrl_externo_se_sanea_a_la_raiz()
    {
        var usuario = NuevoUsuario();
        var contexto = ContextoAutenticado(usuario.Id);

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "ca-ES", "//atacante.example", contexto, new UsuariosFalsos(usuario), new DesenganchadorFalso(), Textos(), NullLogger.Instance);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/");
    }

    [Fact]
    public async Task Un_idioma_fuera_de_la_lista_blanca_se_rechaza_sin_tocar_la_cuenta_ni_la_cookie()
    {
        var usuario = NuevoUsuario();
        var usuarios = new UsuariosFalsos(usuario);
        var contexto = ContextoAutenticado(usuario.Id);

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "en-US", "/", contexto, usuarios, new DesenganchadorFalso(), Textos(), NullLogger.Instance);

        resultado.Should().BeOfType<BadRequest>();
        usuarios.IntentosDeGuardado.Should().Be(0);
        contexto.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public async Task Sin_usuario_autenticado_no_se_toca_nada()
    {
        var usuario = NuevoUsuario();
        var usuarios = new UsuariosFalsos(usuario);
        var contexto = new DefaultHttpContext();

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "ca-ES", "/", contexto, usuarios, new DesenganchadorFalso(), Textos(), NullLogger.Instance);

        resultado.Should().BeOfType<UnauthorizedHttpResult>();
        usuarios.IntentosDeGuardado.Should().Be(0);
        contexto.Response.Headers.SetCookie.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_conflicto_de_concurrencia_se_reintenta_una_vez_sobre_la_fila_recargada()
    {
        var usuario = NuevoUsuario();
        var usuarios = new UsuariosFalsos(usuario) { ConflictosPendientes = 1 };
        var desenganchador = new DesenganchadorFalso();
        var contexto = ContextoAutenticado(usuario.Id);

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "ca-ES", "/", contexto, usuarios, desenganchador, Textos(), NullLogger.Instance);

        usuarios.IntentosDeGuardado.Should().Be(2);
        desenganchador.Desenganchados.Should().ContainSingle().Which.Should().BeSameAs(usuario,
            "sin desenganchar, FindByIdAsync devolvería la misma instancia obsoleta del mapa de identidad");
        usuarios.Guardados.Should().ContainSingle().Which.Should().Be(IdiomaPreferido.Catalan);
        resultado.Should().BeOfType<RedirectHttpResult>();
        UnicoSetCookie(contexto).Should().Contain("ca-ES");
    }

    [Fact]
    public async Task Si_los_dos_intentos_fallan_no_hay_cookie_ni_respuesta_de_exito()
    {
        var usuario = NuevoUsuario();
        var usuarios = new UsuariosFalsos(usuario) { ConflictosPendientes = 2 };
        var contexto = ContextoAutenticado(usuario.Id);

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "ca-ES", "/", contexto, usuarios, new DesenganchadorFalso(), Textos(), NullLogger.Instance);

        usuarios.IntentosDeGuardado.Should().Be(2, "un solo reintento, no más");
        usuarios.Guardados.Should().BeEmpty();
        contexto.Response.Headers.SetCookie.Should().BeEmpty();
        resultado.Should().NotBeOfType<RedirectHttpResult>();
        resultado.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    [Fact]
    public async Task Un_fallo_que_no_es_de_concurrencia_no_se_reintenta_ni_escribe_cookie()
    {
        var usuario = NuevoUsuario();
        var usuarios = new UsuariosFalsos(usuario) { FalloNoConcurrente = true };
        var contexto = ContextoAutenticado(usuario.Id);

        var resultado = await IdiomaEndpoints.CambiarAsync(
            "ca-ES", "/", contexto, usuarios, new DesenganchadorFalso(), Textos(), NullLogger.Instance);

        usuarios.IntentosDeGuardado.Should().Be(1);
        contexto.Response.Headers.SetCookie.Should().BeEmpty();
        resultado.Should().BeAssignableTo<IStatusCodeHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status409Conflict);
    }

    // ── Recursos neutral / ca-ES ──────────────────────────────────────────

    [Fact]
    public void TextosComunes_tiene_recurso_satelite_ca_ES_incrustado()
    {
        var satelite = typeof(TextosComunes).Assembly.GetSatelliteAssembly(new CultureInfo("ca-ES"));

        satelite.GetManifestResourceNames().Should().Contain("CaeManager.Web.Recursos.TextosComunes.ca-ES.resources");
    }

    [Theory]
    [InlineData("es-ES")]
    [InlineData("ca-ES")]
    public void TextosComunes_resuelve_sus_claves_en_cada_cultura_soportada(string cultura)
    {
        var culturaUiPrevia = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo(cultura);

            var texto = Textos()["Volver"];

            texto.ResourceNotFound.Should().BeFalse();
            texto.Value.Should().Be("Volver");
        }
        finally
        {
            CultureInfo.CurrentUICulture = culturaUiPrevia;
        }
    }

    // ── Ayudas ────────────────────────────────────────────────────────────

    private static async Task<(string Cultura, string CulturaUi, string? ContentLanguage)> ResolverAsync(
        Action<HttpRequest> preparar)
    {
        var opciones = new RequestLocalizationOptions();
        CulturaUsuarioCookie.ConfigurarLocalizacion(opciones);

        string? cultura = null, culturaUi = null;
        var middleware = new RequestLocalizationMiddleware(
            _ =>
            {
                cultura = CultureInfo.CurrentCulture.Name;
                culturaUi = CultureInfo.CurrentUICulture.Name;
                return Task.CompletedTask;
            },
            Opciones.Create(opciones),
            NullLoggerFactory.Instance);

        var contexto = new DefaultHttpContext();
        preparar(contexto.Request);
        await middleware.Invoke(contexto);

        return (cultura!, culturaUi!, contexto.Response.Headers.ContentLanguage.ToString());
    }

    private static string CabeceraCookie(string cultura) =>
        $"{CookieRequestCultureProvider.DefaultCookieName}=" +
        Uri.EscapeDataString(CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(cultura, cultura)));

    // Formato de SetCookieHeaderValue: atributos en minúsculas ("path=/; httponly").
    private static string UnicoSetCookie(HttpContext contexto) =>
        contexto.Response.Headers.SetCookie.Should().ContainSingle().Subject!;

    private static DateTimeOffset? ExtraerCaducidad(string setCookie)
    {
        var atributo = setCookie.Split(';').Select(p => p.Trim())
            .FirstOrDefault(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase));
        return atributo is null ? null : DateTimeOffset.Parse(atributo["expires=".Length..], CultureInfo.InvariantCulture);
    }

    private static IStringLocalizer<TextosComunes> Textos() =>
        new StringLocalizer<TextosComunes>(new ResourceManagerStringLocalizerFactory(
            Opciones.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    private static ApplicationUser NuevoUsuario() =>
        new() { Id = Guid.NewGuid(), UserName = "gestora@refrielectric.test", Idioma = IdiomaPreferido.Espanol };

    private static DefaultHttpContext ContextoAutenticado(Guid usuarioId) => new()
    {
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, usuarioId.ToString())], "Prueba")),
    };

    /// <summary>
    /// <c>UserManager</c> con <c>FindByIdAsync</c>/<c>UpdateAsync</c>
    /// controlados: cada <c>FindByIdAsync</c> tras un desenganche devuelve una
    /// instancia nueva (la «fila fresca»), y <c>UpdateAsync</c> puede devolver
    /// un <c>ConcurrencyFailure</c> —como hace <c>UserStore</c>, sin lanzar—.
    /// </summary>
    private sealed class UsuariosFalsos(ApplicationUser usuario) : UserManager<ApplicationUser>(
        new AlmacenNoUsado(), Opciones.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        private int _lecturas;
        public int ConflictosPendientes { get; set; }
        public bool FalloNoConcurrente { get; init; }
        public int IntentosDeGuardado { get; private set; }
        public List<IdiomaPreferido> Guardados { get; } = [];

        public override Task<ApplicationUser?> FindByIdAsync(string userId) =>
            Task.FromResult<ApplicationUser?>(userId == usuario.Id.ToString()
                ? (_lecturas++ == 0 ? usuario : new ApplicationUser { Id = usuario.Id, UserName = usuario.UserName })
                : null);

        public override Task<IdentityResult> UpdateAsync(ApplicationUser user)
        {
            IntentosDeGuardado++;
            if (FalloNoConcurrente)
                return Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName" }));
            if (ConflictosPendientes > 0)
            {
                ConflictosPendientes--;
                return Task.FromResult(IdentityResult.Failed(new IdentityErrorDescriber().ConcurrencyFailure()));
            }

            Guardados.Add(user.Idioma);
            return Task.FromResult(IdentityResult.Success);
        }
    }

    private sealed class AlmacenNoUsado : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class DesenganchadorFalso : IDesenganchadorDeEntidadesRastreadas
    {
        public List<object> Desenganchados { get; } = [];
        public void Desenganchar<TEntidad>(TEntidad entidad) where TEntidad : class => Desenganchados.Add(entidad);
    }
}
