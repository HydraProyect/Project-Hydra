using System.Net;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Account;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
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
    public void Sin_cuenta_se_crea_en_el_Tenant_destino()
    {
        InicioSesionMicrosoft.DecidirCuenta(null, null, false, TenantDestino)
            .Should().Be(DecisionCuentaMicrosoft.CrearCuenta);
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
}
