using System.Net;
using System.Security.Claims;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CaeManager.Web.Tests;

/// <summary>
/// Inicio de sesión corporativo con Microsoft Entra ID sin base de datos ni
/// credenciales: la regla de con qué cuenta entra una identidad de Microsoft, el
/// gate de configuración y la redirección real del manejador OpenID Connect hacia
/// Microsoft (servidor Kestrel en 127.0.0.1, metadatos estáticos: no sale a la red).
/// </summary>
public class InicioSesionMicrosoftTests
{
    [Theory]
    [InlineData("sin-mfa", false)]
    [InlineData("sin-cuenta", false)]
    [InlineData("invitada", false)]
    [InlineData("sin-mfa", true)]
    [InlineData("sin-cuenta", true)]
    [InlineData("invitada", true)]
    public async Task El_callback_rechaza_la_cookie_externa_de_prueba_o_manipulada(string caso, bool manipulada)
    {
        var usuarios = new UsuariosConVinculos
        {
            DuenaDeLaIdentidad = caso == "sin-cuenta" ? null : new ApplicationUser { TenantId = TenantDestino }
        };
        var vigilancia = new VigilanciaDePrueba();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ExternalScheme)
            .AddCookie(IdentityConstants.TwoFactorUserIdScheme);
        builder.Services.AddIdentityCore<ApplicationUser>().AddSignInManager();
        builder.Services.AddSingleton<UserManager<ApplicationUser>>(usuarios);
        builder.Services.Configure<AzureAdOptions>(o => o.TenantHydraId = TenantDestino);
        builder.Services.AddSingleton<CaeManager.Infrastructure.Seguridad.IVigilanciaInicioSesion>(vigilancia);
        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapIdentityEndpoints();
        // Solo el arnés emite esta cookie sintética; el callback y su autenticación son reales.
        app.MapGet("/externa-de-prueba", async (HttpContext contexto) =>
        {
            var principal = Externo((ClaimTypes.NameIdentifier, "sub-prueba"),
                (ClaimTypes.Email, "prueba@ejemplo.test"), ("amr", "pwd"),
                ("acct", caso == "invitada" ? "1" : "0"));
            var propiedades = new AuthenticationProperties();
            propiedades.Items["LoginProvider"] = IdentityEndpointsExtensions.EsquemaMicrosoft;
            await contexto.SignInAsync(IdentityConstants.ExternalScheme, principal, propiedades);
        });
        await app.StartAsync();
        using var cliente = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false
        }) { BaseAddress = new Uri(app.Urls.Single()) };
        using var emision = await cliente.GetAsync("/externa-de-prueba");
        var cookie = emision.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        if (manipulada) cookie = cookie[..(cookie.IndexOf('=') + 1)] + "cookie-falsa-manipulada";
        using var peticion = new HttpRequestMessage(HttpMethod.Get, "/cuenta/microsoft-callback?returnUrl=%2Fdocumentos");
        peticion.Headers.Add("Cookie", cookie);

        using var respuesta = await cliente.SendAsync(peticion);

        respuesta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var error = manipulada || caso == "invitada" ? "fallo" : caso;
        respuesta.Headers.Location!.OriginalString.Should().Be($"/cuenta/iniciar-sesion?errorSso={error}");
        usuarios.LlamadasAlta.Should().Be(0, "ningún rechazo puede guardar el vínculo");
        respuesta.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c =>
            c.StartsWith(".AspNetCore.Identity.Application=", StringComparison.Ordinal)
            && !c.StartsWith(".AspNetCore.Identity.Application=;", StringComparison.Ordinal),
            "ningún rechazo emite una sesión de aplicación");
        vigilancia.Resultados.Should().Equal(manipulada ? [] :
            [caso == "sin-mfa" ? CaeManager.Domain.Seguridad.ResultadoInicioSesion.Rechazado : CaeManager.Domain.Seguridad.ResultadoInicioSesion.Fallido]);
        await app.StopAsync();
    }

    private sealed class VigilanciaDePrueba : CaeManager.Infrastructure.Seguridad.IVigilanciaInicioSesion
    {
        public List<CaeManager.Domain.Seguridad.ResultadoInicioSesion> Resultados { get; } = [];
        public Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default) => Task.FromResult(true);
        public Task RegistrarAsync(Guid? usuarioId, Guid? tenantId, CaeManager.Domain.Seguridad.MetodoInicioSesion metodo,
            CaeManager.Domain.Seguridad.ResultadoInicioSesion resultado,
            CaeManager.Domain.Seguridad.SenalesInicioSesion senalesConocidas = CaeManager.Domain.Seguridad.SenalesInicioSesion.Ninguna,
            CancellationToken ct = default)
        {
            Resultados.Add(resultado);
            return Task.CompletedTask;
        }
        public Task<CaeManager.Infrastructure.Seguridad.ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(
            string usuarioId, string codigo, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData("acct", "1", false, false)]
    [InlineData("acct", "1", true, true)]
    [InlineData("acct", "0", false, true)]
    [InlineData("otro", "1", false, true)]
    [InlineData("http://schemas.microsoft.com/ws/2010/07/identity/claims/accounttype", "1", false, false)]
    public void La_cuenta_invitada_requiere_configuracion_explicita(string tipo, string valor, bool permitir, bool esperado)
    {
        var opciones = new AzureAdOptions();
        opciones.PermitirInvitados.Should().BeFalse();
        opciones.PermitirInvitados = permitir;
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(tipo, valor)], "Microsoft"));
        InicioSesionMicrosoft.PermiteCuenta(principal, opciones).Should().Be(esperado);
    }

    private static readonly Guid TenantDestino = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid OtroTenant = Guid.Parse("00000000-0000-0000-0000-0000000000bb");

    // ── Con qué cuenta entra una identidad de Microsoft ─────────────────────

    [Fact]
    public void Identidad_ya_vinculada_a_una_cuenta_del_Tenant_destino_entra()
    {
        InicioSesionMicrosoft.DecidirCuenta(TenantDestino, null, false, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.EntrarConCuentaVinculada);
    }

    [Fact]
    public void Identidad_vinculada_a_una_cuenta_de_otro_Tenant_no_entra()
    {
        InicioSesionMicrosoft.DecidirCuenta(OtroTenant, null, false, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant);
    }

    [Fact]
    public void El_vinculo_manda_sobre_el_correo()
    {
        // Aunque el correo apunte a otra cuenta, la identidad vinculada decide.
        InicioSesionMicrosoft.DecidirCuenta(TenantDestino, OtroTenant, true, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.EntrarConCuentaVinculada);
    }

    [Fact]
    public void Coincidencia_de_correo_con_una_cuenta_de_otro_Tenant_no_vincula_ni_entra()
    {
        // El caso que este cambio cierra: el correo de Entra es editable, y antes
        // bastaba con que coincidiera para entrar en la cuenta de otra organización
        // (o de Soporte TALVEG) sin su contraseña ni su segundo factor.
        InicioSesionMicrosoft.DecidirCuenta(null, OtroTenant, false, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant);
    }

    [Fact]
    public void Coincidencia_de_correo_con_una_cuenta_ya_vinculada_a_otra_identidad_no_entra()
    {
        InicioSesionMicrosoft.DecidirCuenta(null, TenantDestino, true, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.RechazarCuentaVinculadaAOtraIdentidad);
    }

    [Fact]
    public void Coincidencia_de_correo_en_el_Tenant_destino_sin_otra_identidad_vincula()
    {
        InicioSesionMicrosoft.DecidirCuenta(null, TenantDestino, false, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.VincularCuentaExistente);
    }

    [Fact]
    public void Sin_cuenta_previa_se_rechaza_y_no_se_crea_nada()
    {
        // Invitación previa (D-3): antes había alta automática sin rol, y como el
        // correo de Entra no está verificado, abría cuenta a cualquiera del directorio.
        InicioSesionMicrosoft.DecidirCuenta(null, null, false, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.RechazarSinCuentaPrevia);
        Enum.GetNames<DecisionCuentaMicrosoft>().Should().NotContain(n => n.Contains("Crear", StringComparison.Ordinal),
            "ninguna decisión del inicio con Microsoft da de alta una cuenta");
    }

    [Theory]
    [InlineData(DecisionCuentaMicrosoft.EntrarConCuentaVinculada)]
    [InlineData(DecisionCuentaMicrosoft.VincularCuentaExistente)]
    public void Una_cuenta_desactivada_ni_entra_ni_se_vincula(DecisionCuentaMicrosoft decision)
    {
        var ahora = DateTimeOffset.UtcNow;
        var activa = new ApplicationUser { TenantId = TenantDestino };
        var desactivada = new ApplicationUser { TenantId = TenantDestino };
        desactivada.Desactivar();

        InicioSesionMicrosoft.ComprobarCuentaActiva(decision, activa, ahora).Should().Be(decision, "control positivo");
        InicioSesionMicrosoft.ComprobarCuentaActiva(decision, desactivada, ahora)
            .Should().Be(DecisionCuentaMicrosoft.RechazarCuentaDesactivada);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sin_Tenant_destino_se_rechaza_siempre(bool conCuentaVinculada)
    {
        InicioSesionMicrosoft.DecidirCuenta(conCuentaVinculada ? Guid.Empty : null, null, false, Guid.Empty)
            .Should().Be(DecisionCuentaMicrosoft.RechazarSinTenantDestino);
    }

    // ── Gate de configuración ───────────────────────────────────────────────

    [Fact]
    public void Sin_TenantHydraId_el_esquema_sigue_registrado_y_se_avisa_de_que_falta()
    {
        // Fallo cerrado: quitarlo no puede apagar el gate, porque apagarlo levanta
        // la restricción del login local. El callback rechaza a todos y se avisa.
        var opciones = OpcionesCompletas();
        opciones.ProblemasDeConfiguracion().Should().BeEmpty("control positivo");

        foreach (Guid? sinTenant in new Guid?[] { null, Guid.Empty })
        {
            opciones.TenantHydraId = sinTenant;
            opciones.EstaConfigurado.Should().BeTrue();
            opciones.TieneTenantDestino.Should().BeFalse();
            opciones.ProblemasDeConfiguracion().Should().ContainSingle().Which.Should().Be("falta TenantHydraId");
        }
    }

    [Fact]
    public void Sin_ninguna_clave_no_se_avisa_de_TenantHydraId()
    {
        new AzureAdOptions().ProblemasDeConfiguracion().Should().BeEmpty("apagado es lo normal");
    }

    // ── Redirección a Microsoft ─────────────────────────────────────────────

    [Fact]
    public async Task El_boton_redirige_a_Microsoft_con_codigo_PKCE_estado_y_nonce()
    {
        var opciones = OpcionesCompletas();
        var autorizacion = $"https://login.microsoftonline.com/{opciones.TenantId}/oauth2/v2.0/authorize";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services
            .AddAuthentication(o =>
            {
                o.DefaultScheme = IdentityConstants.ApplicationScheme;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddCookie(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ExternalScheme)
            .AddOpenIdConnect(IdentityEndpointsExtensions.EsquemaMicrosoft, o =>
            {
                InicioSesionMicrosoft.Configurar(o, opciones);
                // Metadatos estáticos: el manejador no consulta Microsoft.
                o.Configuration = new OpenIdConnectConfiguration
                {
                    Issuer = $"https://login.microsoftonline.com/{opciones.TenantId}/v2.0",
                    AuthorizationEndpoint = autorizacion,
                    TokenEndpoint = $"https://login.microsoftonline.com/{opciones.TenantId}/oauth2/v2.0/token",
                };
            });

        await using var app = builder.Build();
        app.UseAuthentication();
        app.MapGet("/cuenta/iniciar-sesion-microsoft", (string? returnUrl) => InicioSesionMicrosoft.Desafiar(returnUrl));
        await app.StartAsync();

        var origen = app.Urls.Single();
        using var cliente = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var respuesta = await cliente.GetAsync($"{origen}/cuenta/iniciar-sesion-microsoft?returnUrl=%2Fdocumentos");

        respuesta.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var destino = respuesta.Headers.Location!;
        destino.GetLeftPart(UriPartial.Path).Should().Be(autorizacion);

        var parametros = QueryHelpers.ParseQuery(destino.Query);
        parametros["client_id"].ToString().Should().Be(opciones.ClientId);
        parametros["redirect_uri"].ToString().Should().Be($"{origen}{InicioSesionMicrosoft.RutaRespuesta}");
        parametros["response_type"].ToString().Should().Be("code");
        parametros["scope"].ToString().Split(' ').Should().BeEquivalentTo(["openid", "profile", "email"]);
        parametros["code_challenge_method"].ToString().Should().Be("S256");
        parametros["code_challenge"].ToString().Should().NotBeNullOrWhiteSpace();
        parametros["state"].ToString().Should().NotBeNullOrWhiteSpace();
        parametros["nonce"].ToString().Should().NotBeNullOrWhiteSpace();
        parametros.Should().NotContainKey("client_secret", "el secreto nunca viaja en la redirección del navegador");
        destino.Query.Should().NotContain(opciones.ClientSecret!);

        // Nonce y correlación en cookies HttpOnly: sin ellas no se valida la vuelta.
        var cookies = respuesta.Headers.GetValues("Set-Cookie").ToList();
        cookies.Should().Contain(c => c.StartsWith(".AspNetCore.OpenIdConnect.Nonce.", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        cookies.Should().Contain(c => c.StartsWith(".AspNetCore.Correlation.", StringComparison.Ordinal) && c.Contains("httponly", StringComparison.OrdinalIgnoreCase));

        await app.StopAsync();
    }

    [Fact]
    public void La_autoridad_es_la_del_directorio_configurado()
    {
        var opciones = OpcionesCompletas();
        var oidc = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions();

        InicioSesionMicrosoft.Configurar(oidc, opciones);

        oidc.Authority.Should().Be($"https://login.microsoftonline.com/{opciones.TenantId}/v2.0");
        oidc.UsePkce.Should().BeTrue();
        oidc.SaveTokens.Should().BeFalse("los tokens de Microsoft no se guardan en la cookie de TALVEG");
        oidc.ProtocolValidator.RequireNonce.Should().BeTrue();
        oidc.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
    }

    private static AzureAdOptions OpcionesCompletas() => new()
    {
        TenantId = "9b1c2d3e-0000-4000-8000-00000000c0de",
        ClientId = "11111111-2222-3333-4444-555555555555",
        ClientSecret = "valor-ficticio-no-es-un-secreto",
        TenantHydraId = TenantDestino,
    };

    // ── Segundo factor (D-2): acrs, si no amr con mfa, si no el TOTP de TALVEG ──

    private static ClaimsPrincipal Externo(params (string Tipo, string Valor)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Tipo, c.Valor)), "Microsoft"));

    [Fact]
    public void El_contexto_de_autenticacion_configurado_en_acrs_acredita_la_MFA()
    {
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo(("acrs", "c1"), ("amr", "pwd")), "c1").Should().BeTrue();
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo(("acrs", "[\"c2\",\"c1\"]")), "c1").Should().BeTrue("lista JSON");
    }

    [Fact]
    public void Otro_contexto_en_acrs_no_acredita_la_MFA()
    {
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo(("acrs", "c2"), ("amr", "pwd")), "c1").Should().BeFalse();
    }

    [Fact]
    public void Sin_contexto_configurado_acrs_no_cuenta()
    {
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo(("acrs", "c1"), ("amr", "pwd")), null).Should().BeFalse();
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo(("acrs", "c1"), ("amr", "pwd")), " ").Should().BeFalse();
    }

    [Theory]
    [InlineData("amr")]
    [InlineData("http://schemas.microsoft.com/claims/authnmethodsreferences")]
    public void Sin_acrs_la_MFA_se_acredita_con_mfa_en_amr(string tipo)
    {
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo((tipo, "pwd"), (tipo, "mfa")), "c1").Should().BeTrue();
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo((tipo, "[\"pwd\",\"mfa\"]")), null).Should().BeTrue("lista JSON");
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo((tipo, "pwd")), null).Should().BeFalse("solo contraseña");
    }

    [Fact]
    public void Sin_acrs_ni_amr_no_hay_MFA_de_Microsoft()
    {
        InicioSesionMicrosoft.MfaAcreditadaPorMicrosoft(Externo(("email", "mfa@ejemplo.test")), "c1").Should().BeFalse();
    }

    [Theory]
    [InlineData(true, true, SegundoFactorMicrosoft.SesionDirecta)]
    [InlineData(true, false, SegundoFactorMicrosoft.SesionDirecta)]
    [InlineData(false, true, SegundoFactorMicrosoft.PedirTotp)]
    [InlineData(false, false, SegundoFactorMicrosoft.RechazarSinSegundoFactor)]
    public void Sin_MFA_de_Microsoft_se_pide_el_TOTP_y_sin_TOTP_no_hay_sesion(bool mfa, bool totp, SegundoFactorMicrosoft esperado)
    {
        InicioSesionMicrosoft.DecidirSegundoFactor(mfa, totp, vinculoNuevo: false).Should().Be(esperado);
    }

    [Theory]
    [InlineData(true, true, SegundoFactorMicrosoft.PedirTotp)]
    [InlineData(false, true, SegundoFactorMicrosoft.PedirTotp)]
    [InlineData(true, false, SegundoFactorMicrosoft.SesionDirecta)]
    [InlineData(false, false, SegundoFactorMicrosoft.RechazarSinSegundoFactor)]
    public void Un_vinculo_nuevo_a_una_cuenta_con_TOTP_pasa_por_el_TOTP_aunque_haya_MFA_de_Microsoft(
        bool mfa, bool totp, SegundoFactorMicrosoft esperado)
    {
        InicioSesionMicrosoft.DecidirSegundoFactor(mfa, totp, vinculoNuevo: true).Should().Be(esperado);
    }

    [Fact]
    public void El_vinculo_pendiente_viaja_en_la_cookie_de_dos_pasos_junto_a_la_cuenta()
    {
        var id = Guid.NewGuid();
        var principal = InicioSesionMicrosoft.PrincipalSegundoFactorPendiente(id,
            new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-1", "Microsoft (empresa)"));

        principal.FindFirstValue(ClaimTypes.Name).Should().Be(id.ToString());
        var vinculo = InicioSesionMicrosoft.VinculoPendiente(principal);
        vinculo.Should().NotBeNull();
        vinculo!.LoginProvider.Should().Be(IdentityEndpointsExtensions.EsquemaMicrosoft);
        vinculo.ProviderKey.Should().Be("sub-1");
        vinculo.ProviderDisplayName.Should().Be("Microsoft (empresa)");

        InicioSesionMicrosoft.VinculoPendiente(InicioSesionMicrosoft.PrincipalSegundoFactorPendiente(id, null))
            .Should().BeNull("una identidad ya vinculada no deja vínculo pendiente");
        InicioSesionMicrosoft.VinculoPendiente(null).Should().BeNull();
    }

    [Fact]
    public void Un_vinculo_pendiente_en_una_cookie_de_dos_pasos_que_no_es_de_Microsoft_se_ignora()
    {
        var identidad = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identidad.AddClaim(new Claim(ClaimTypes.Name, Guid.NewGuid().ToString()));
        identidad.AddClaim(new Claim(InicioSesionMicrosoft.TipoClaimVinculoPendiente, "sub-1"));

        InicioSesionMicrosoft.VinculoPendiente(new ClaimsPrincipal(identidad)).Should().BeNull();
    }

    [Fact]
    public void Solo_se_deja_pendiente_un_vinculo_de_Microsoft()
    {
        var accion = () => InicioSesionMicrosoft.PrincipalSegundoFactorPendiente(Guid.NewGuid(), new UserLoginInfo("Otro", "k", "Otro"));

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Tras_el_TOTP_se_vincula_si_la_cuenta_no_tiene_otra_identidad_de_Microsoft()
    {
        const string esquema = IdentityEndpointsExtensions.EsquemaMicrosoft;

        InicioSesionMicrosoft.DecidirVinculoTrasTotp([], "sub-1").Should().Be(DecisionVinculoTrasTotp.Vincular);
        InicioSesionMicrosoft.DecidirVinculoTrasTotp([new UserLoginInfo("Otro", "sub-2", "Otro")], "sub-1")
            .Should().Be(DecisionVinculoTrasTotp.Vincular);
        InicioSesionMicrosoft.DecidirVinculoTrasTotp([new UserLoginInfo(esquema, "sub-1", esquema)], "sub-1")
            .Should().Be(DecisionVinculoTrasTotp.YaVinculada);
        InicioSesionMicrosoft.DecidirVinculoTrasTotp([new UserLoginInfo(esquema, "sub-2", esquema)], "sub-1")
            .Should().Be(DecisionVinculoTrasTotp.RechazarOtraIdentidad, "una cuenta no lleva dos identidades de Microsoft");
    }

    [Fact]
    public void El_contexto_de_autenticacion_se_pide_a_Microsoft_en_el_token_de_ID()
    {
        InicioSesionMicrosoft.SolicitudContextoAutenticacion(null).Should().BeNull();
        InicioSesionMicrosoft.SolicitudContextoAutenticacion("").Should().BeNull();
        InicioSesionMicrosoft.SolicitudContextoAutenticacion(" c1 ").Should()
            .Be("{\"id_token\":{\"acrs\":{\"essential\":true,\"value\":\"c1\"}}}");
    }

    [Theory]
    [InlineData("c1", "{\"id_token\":{\"acrs\":{\"essential\":true,\"value\":\"c1\"}}}")]
    [InlineData(null, null)]
    public async Task Con_contexto_configurado_la_redireccion_a_Microsoft_lo_pide(string? contexto, string? esperado)
    {
        var opciones = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions();
        InicioSesionMicrosoft.Configurar(opciones, new AzureAdOptions
        {
            TenantId = "directorio",
            ClientId = "cliente",
            ClientSecret = "secreto",
            ContextoAutenticacionMfa = contexto,
        });
        var redireccion = new Microsoft.AspNetCore.Authentication.OpenIdConnect.RedirectContext(
            new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme(IdentityEndpointsExtensions.EsquemaMicrosoft, null,
                typeof(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler)),
            opciones, new Microsoft.AspNetCore.Authentication.AuthenticationProperties())
        {
            ProtocolMessage = new OpenIdConnectMessage(),
        };

        await opciones.Events.RedirectToIdentityProvider(redireccion);

        redireccion.ProtocolMessage.GetParameter("claims").Should().Be(esperado);
    }

    [Fact]
    public void El_paso_del_TOTP_lleva_la_cuenta_y_el_proveedor_como_lo_escribe_Identity()
    {
        var id = Guid.NewGuid();
        var principal = InicioSesionMicrosoft.PrincipalSegundoFactorPendiente(id, null);

        principal.Identity!.AuthenticationType.Should().Be(IdentityConstants.TwoFactorUserIdScheme);
        principal.FindFirstValue(ClaimTypes.Name).Should().Be(id.ToString());
        principal.FindFirstValue(ClaimTypes.AuthenticationMethod).Should().Be(IdentityEndpointsExtensions.EsquemaMicrosoft);
    }

    [Fact]
    public void La_sesion_que_emite_Identity_tras_el_TOTP_del_inicio_con_Microsoft_lleva_la_marca_SSO()
    {
        var marca = new Claim(RestriccionLoginLocalClaimsTransformation.TipoClaimMetodoLogin, RestriccionLoginLocalClaimsTransformation.MetodoLoginSso);

        SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft(
                [new Claim("amr", "mfa"), new Claim(ClaimTypes.AuthenticationMethod, IdentityEndpointsExtensions.EsquemaMicrosoft)])
            .Should().ContainSingle(c => c.Type == marca.Type && c.Value == marca.Value);

        SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft([new Claim("amr", "mfa")])
            .Should().NotContain(c => c.Type == marca.Type, "el TOTP de un inicio con contraseña no es SSO");
        SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft([new Claim(ClaimTypes.AuthenticationMethod, "Otro")])
            .Should().NotContain(c => c.Type == marca.Type);
        SignInManagerCuentaDesactivada.ConMarcaDeInicioConMicrosoft(
                [marca, new Claim(ClaimTypes.AuthenticationMethod, IdentityEndpointsExtensions.EsquemaMicrosoft)])
            .Should().ContainSingle(c => c.Type == marca.Type, "no se duplica");
    }

    [Fact]
    public async Task Tras_el_TOTP_el_vinculo_pendiente_se_guarda_en_la_cuenta()
    {
        var usuarios = new UsuariosConVinculos();
        var cuenta = new ApplicationUser { Id = Guid.NewGuid() };
        var vinculo = new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-1", "Microsoft (empresa)");

        (await InicioSesionMicrosoft.CompletarVinculoPendienteAsync(usuarios, cuenta, vinculo)).Should().BeTrue();
        usuarios.Vinculos.Should().ContainSingle(v => v.ProviderKey == "sub-1");

        (await InicioSesionMicrosoft.CompletarVinculoPendienteAsync(usuarios, cuenta, vinculo)).Should().BeTrue("ya vinculada");
        usuarios.LlamadasAlta.Should().Be(1, "no se vuelve a guardar");
    }

    [Fact]
    public async Task Tras_el_TOTP_no_se_anade_una_segunda_identidad_de_Microsoft()
    {
        var usuarios = new UsuariosConVinculos();
        usuarios.Vinculos.Add(new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-2", "Microsoft (empresa)"));

        (await InicioSesionMicrosoft.CompletarVinculoPendienteAsync(usuarios, new ApplicationUser { Id = Guid.NewGuid() },
            new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-1", "Microsoft (empresa)"))).Should().BeFalse();
        usuarios.LlamadasAlta.Should().Be(0);
    }

    [Fact]
    public async Task Tras_el_TOTP_un_vinculo_que_no_se_guarda_no_cuenta_como_vinculado()
    {
        var usuarios = new UsuariosConVinculos { FallarAlta = true };

        (await InicioSesionMicrosoft.CompletarVinculoPendienteAsync(usuarios, new ApplicationUser { Id = Guid.NewGuid() },
            new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-1", "Microsoft (empresa)"))).Should().BeFalse();
    }

    [Fact]
    public async Task Tras_el_TOTP_una_identidad_ya_vinculada_a_otra_cuenta_no_se_vincula_ni_lanza()
    {
        // Otra cuenta se quedó la identidad entre el callback y el TOTP: no se vincula
        // ni se intenta guardar (la clave del vínculo es única y el choque lanzaría).
        var usuarios = new UsuariosConVinculos { DuenaDeLaIdentidad = new ApplicationUser { Id = Guid.NewGuid() } };

        (await InicioSesionMicrosoft.CompletarVinculoPendienteAsync(usuarios, new ApplicationUser { Id = Guid.NewGuid() },
            new UserLoginInfo(IdentityEndpointsExtensions.EsquemaMicrosoft, "sub-1", "Microsoft (empresa)"))).Should().BeFalse();
        usuarios.LlamadasAlta.Should().Be(0);
    }

    /// <summary>Solo los vínculos de una cuenta, en memoria: lo único que toca el vínculo tras el TOTP.</summary>
    private sealed class UsuariosConVinculos() : UserManager<ApplicationUser>(
        new AlmacenSinUso(), Microsoft.Extensions.Options.Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(),
        [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        public List<UserLoginInfo> Vinculos { get; } = [];
        public bool FallarAlta { get; init; }
        public ApplicationUser? DuenaDeLaIdentidad { get; init; }
        public int LlamadasAlta { get; private set; }

        public override Task<IList<UserLoginInfo>> GetLoginsAsync(ApplicationUser user) =>
            Task.FromResult<IList<UserLoginInfo>>([.. Vinculos]);

        public override Task<ApplicationUser?> FindByLoginAsync(string loginProvider, string providerKey) =>
            Task.FromResult(DuenaDeLaIdentidad);

        public override Task<ApplicationUser?> FindByEmailAsync(string email) => Task.FromResult<ApplicationUser?>(null);

        public override Task<bool> GetTwoFactorEnabledAsync(ApplicationUser user) => Task.FromResult(false);

        public override Task<IdentityResult> AddLoginAsync(ApplicationUser user, UserLoginInfo login)
        {
            LlamadasAlta++;
            if (FallarAlta)
                return Task.FromResult(IdentityResult.Failed(new IdentityError { Description = "fallo" }));
            Vinculos.Add(login);
            return Task.FromResult(IdentityResult.Success);
        }
    }

    private sealed class AlmacenSinUso : IUserStore<ApplicationUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
