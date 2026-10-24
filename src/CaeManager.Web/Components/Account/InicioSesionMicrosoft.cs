using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace CaeManager.Web.Components.Account;

/// <summary>
/// Piezas del inicio de sesión corporativo con Microsoft Entra ID que no necesitan
/// base de datos: la configuración del esquema OpenID Connect y la regla que decide
/// con qué cuenta de TALVEG entra una identidad de Microsoft. Separadas del
/// callback (<see cref="IdentityEndpointsExtensions"/>) para poder probarlas sin
/// servidor ni PostgreSQL.
/// </summary>
public static class InicioSesionMicrosoft
{
    /// <summary>Ruta a la que Microsoft devuelve la respuesta de autorización. Es el redirect URI que se registra en Entra.</summary>
    public const string RutaRespuesta = "/signin-microsoft";

    /// <summary>
    /// Configura el esquema de Program.cs. Flujo de código con PKCE, estado y nonce
    /// (valores por defecto del manejador, fijados aquí para que no dependan de
    /// ellos), autoridad del directorio configurado —nunca <c>common</c> ni
    /// <c>organizations</c>— y sin guardar tokens de Microsoft en la cookie.
    /// </summary>
    public static void Configurar(OpenIdConnectOptions options, AzureAdOptions azureAd)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(azureAd);

        options.Authority = $"{azureAd.Instance}{azureAd.TenantId}/v2.0";
        options.ClientId = azureAd.ClientId;
        options.ClientSecret = azureAd.ClientSecret;
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.CallbackPath = RutaRespuesta;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("email");
    }

    /// <summary>Desafío del botón «continuar con Microsoft»: tras la respuesta, el navegador vuelve al callback de la cuenta.</summary>
    public static IResult Desafiar(string? returnUrl)
    {
        var destino = "/cuenta/microsoft-callback";
        if (!string.IsNullOrWhiteSpace(returnUrl))
            destino += $"?returnUrl={Uri.EscapeDataString(returnUrl)}";

        return Results.Challenge(new AuthenticationProperties { RedirectUri = destino }, [IdentityEndpointsExtensions.EsquemaMicrosoft]);
    }

    /// <summary>
    /// Busca las cuentas candidatas y aplica <see cref="DecidirCuenta"/>. Debe
    /// llamarse dentro de <see cref="AmbitoIdentificacionSinTenant"/>: el vínculo
    /// (<c>AspNetUserLogins</c>) y el correo se resuelven antes de que haya Tenant.
    /// Devuelve la cuenta con la que se entra, la que hay que vincular o, en un
    /// rechazo, la que se intentó abrir (para la vigilancia de su Tenant).
    /// </summary>
    public static async Task<(DecisionCuentaMicrosoft Decision, ApplicationUser? Cuenta)> ResolverCuentaAsync(
        UserManager<ApplicationUser> userManager, UserLoginInfo identidad, string email, Guid tenantDestino)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(identidad);

        var vinculada = await userManager.FindByLoginAsync(identidad.LoginProvider, identidad.ProviderKey);
        ApplicationUser? porCorreo = null;
        var porCorreoTieneOtraIdentidad = false;
        if (vinculada is null)
        {
            porCorreo = await userManager.FindByEmailAsync(email);
            if (porCorreo is not null)
                porCorreoTieneOtraIdentidad = (await userManager.GetLoginsAsync(porCorreo))
                    .Any(l => l.LoginProvider == identidad.LoginProvider);
        }

        var decision = DecidirCuenta(vinculada?.TenantId, porCorreo?.TenantId, porCorreoTieneOtraIdentidad, tenantDestino);
        var cuenta = decision switch
        {
            DecisionCuentaMicrosoft.EntrarConCuentaVinculada => vinculada,
            DecisionCuentaMicrosoft.VincularCuentaExistente => porCorreo,
            DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant or DecisionCuentaMicrosoft.RechazarCuentaVinculadaAOtraIdentidad => vinculada ?? porCorreo,
            _ => null,
        };
        return (decision, cuenta);
    }

    /// <summary>
    /// Con qué cuenta de TALVEG entra una identidad de Microsoft.
    ///
    /// <para>
    /// Primero manda el vínculo ya guardado (proveedor + clave de la identidad, que
    /// no cambia aunque cambie el correo). Solo sin vínculo se mira el correo, y el
    /// correo de Entra es un atributo editable por quien administra el directorio:
    /// no prueba que la persona sea la dueña de una cuenta de TALVEG. Por eso una
    /// coincidencia de correo solo se acepta en el Tenant propietario declarado para
    /// este inicio de sesión (<see cref="AzureAdOptions.TenantHydraId"/>) y solo si
    /// esa cuenta no está ya vinculada a otra identidad de Microsoft. Fuera de ese
    /// Tenant —otra organización, Soporte TALVEG o cualquier Actor de Plataforma— no
    /// se entra nunca por Microsoft, ni con vínculo previo.
    /// </para>
    /// </summary>
    /// <param name="tenantCuentaVinculada">Tenant propietario de la cuenta ya vinculada a esta identidad, si la hay.</param>
    /// <param name="tenantCuentaPorCorreo">Tenant propietario de la cuenta con el mismo correo, si la hay (solo se consulta sin vínculo).</param>
    /// <param name="cuentaPorCorreoTieneOtraIdentidadMicrosoft">La cuenta con el mismo correo ya está vinculada a otra identidad de Microsoft.</param>
    /// <param name="tenantDestino">El Tenant propietario declarado en <see cref="AzureAdOptions.TenantHydraId"/>.</param>
    public static DecisionCuentaMicrosoft DecidirCuenta(
        Guid? tenantCuentaVinculada, Guid? tenantCuentaPorCorreo, bool cuentaPorCorreoTieneOtraIdentidadMicrosoft, Guid tenantDestino)
    {
        if (tenantDestino == Guid.Empty)
            return DecisionCuentaMicrosoft.RechazarSinTenantDestino;

        if (tenantCuentaVinculada is { } vinculada)
            return vinculada == tenantDestino
                ? DecisionCuentaMicrosoft.EntrarConCuentaVinculada
                : DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant;

        if (tenantCuentaPorCorreo is { } porCorreo)
        {
            if (porCorreo != tenantDestino)
                return DecisionCuentaMicrosoft.RechazarCuentaDeOtroTenant;

            return cuentaPorCorreoTieneOtraIdentidadMicrosoft
                ? DecisionCuentaMicrosoft.RechazarCuentaVinculadaAOtraIdentidad
                : DecisionCuentaMicrosoft.VincularCuentaExistente;
        }

        return DecisionCuentaMicrosoft.CrearCuenta;
    }
}

/// <summary>Resultado de <see cref="InicioSesionMicrosoft.DecidirCuenta"/>.</summary>
public enum DecisionCuentaMicrosoft
{
    /// <summary>La identidad ya estaba vinculada a una cuenta del Tenant destino.</summary>
    EntrarConCuentaVinculada,

    /// <summary>Cuenta del Tenant destino con el mismo correo y sin otra identidad de Microsoft: se vincula.</summary>
    VincularCuentaExistente,

    /// <summary>Ninguna cuenta: alta sin rol en el Tenant destino (sala de espera).</summary>
    CrearCuenta,

    /// <summary>La cuenta encontrada pertenece a otro Tenant propietario: nunca se entra en ella por Microsoft.</summary>
    RechazarCuentaDeOtroTenant,

    /// <summary>La cuenta del correo ya tiene otra identidad de Microsoft: un segundo vínculo sería una toma de cuenta.</summary>
    RechazarCuentaVinculadaAOtraIdentidad,

    /// <summary>Falta <see cref="AzureAdOptions.TenantHydraId"/>: no se sabe a qué Tenant pertenecería nadie.</summary>
    RechazarSinTenantDestino,
}
