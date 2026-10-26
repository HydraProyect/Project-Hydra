using System.Security.Claims;
using System.Text.Json;
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

        // Contexto de autenticación (D-2): se pide en cada inicio para que el Acceso
        // condicional que IT asocie a él se aplique y vuelva en «acrs».
        if (SolicitudContextoAutenticacion(azureAd.ContextoAutenticacionMfa) is { } solicitud)
        {
            options.Events.OnRedirectToIdentityProvider = contexto =>
            {
                contexto.ProtocolMessage.SetParameter("claims", solicitud);
                return Task.CompletedTask;
            };
        }
    }

    /// <summary>
    /// El parámetro <c>claims</c> que pide a Entra el contexto de autenticación en el
    /// token de ID, o <c>null</c> si no hay contexto configurado.
    /// </summary>
    public static string? SolicitudContextoAutenticacion(string? contexto) =>
        string.IsNullOrWhiteSpace(contexto)
            ? null
            : JsonSerializer.Serialize(new { id_token = new { acrs = new { essential = true, value = contexto.Trim() } } });

    // «acct» llega con su nombre; el segundo cubre un mapeo de entrada que lo renombre.
    private static readonly string[] TiposClaimAcct = ["acct", "http://schemas.microsoft.com/ws/2010/07/identity/claims/accounttype"];

    /// <summary>
    /// Si la identidad de Microsoft puede seguir hacia la cuenta de TALVEG: una cuenta
    /// invitada B2B del directorio (<c>acct</c> = 1) solo con
    /// <see cref="AzureAdOptions.PermitirInvitados"/>. <c>acct</c> es una notificación
    /// opcional que el App Registration tiene que emitir en el token de ID: sin ella
    /// (o con 0, miembro) el inicio sigue como antes.
    /// </summary>
    public static bool PermiteCuenta(ClaimsPrincipal principalExterno, AzureAdOptions opciones)
    {
        ArgumentNullException.ThrowIfNull(principalExterno);
        ArgumentNullException.ThrowIfNull(opciones);
        return opciones.PermitirInvitados || !principalExterno.Claims.Any(c =>
            TiposClaimAcct.Contains(c.Type, StringComparer.Ordinal) && c.Value.Trim() == "1");
    }

    // «amr» llega con su nombre o, con el mapeo de entrada del manejador, con este.
    private static readonly string[] TiposClaimAmr = ["amr", "http://schemas.microsoft.com/claims/authnmethodsreferences"];

    /// <summary>
    /// Si el token de Microsoft acredita la MFA (decisión D-2, exactamente en este
    /// orden): el contexto de autenticación configurado en <c>acrs</c>; si no, <c>mfa</c>
    /// en <c>amr</c>. Sin contexto configurado, <c>acrs</c> no se mira.
    /// </summary>
    public static bool MfaAcreditadaPorMicrosoft(ClaimsPrincipal principalExterno, string? contextoAutenticacionMfa)
    {
        ArgumentNullException.ThrowIfNull(principalExterno);

        if (!string.IsNullOrWhiteSpace(contextoAutenticacionMfa)
            && principalExterno.FindAll("acrs").SelectMany(Valores).Contains(contextoAutenticacionMfa.Trim(), StringComparer.Ordinal))
            return true;

        return principalExterno.Claims.Where(c => TiposClaimAmr.Contains(c.Type, StringComparer.Ordinal))
            .SelectMany(Valores).Contains("mfa", StringComparer.Ordinal);
    }

    /// <summary>
    /// Qué hace el callback con el segundo factor: sesión directa si Microsoft acreditó
    /// la MFA; si no, el TOTP de TALVEG por el mismo paso que el inicio con contraseña;
    /// y sin TOTP configurado, rechazo (no se emite sesión).
    /// <para>
    /// Excepción: una identidad que se vincula por primera vez (<paramref name="vinculoNuevo"/>)
    /// a una cuenta con TOTP configurado pasa siempre por el TOTP, aunque Microsoft
    /// acreditara la MFA, y se vincula solo al superarlo (<see cref="CompletarVinculoPendienteAsync"/>).
    /// La MFA de Microsoft prueba quién controla la identidad del directorio, no que
    /// sea la persona de la cuenta: el vínculo se decide por el correo, que en Entra
    /// edita quien administra el directorio. Sin esta excepción, otra identidad del
    /// mismo directorio con ese correo se quedaría con una cuenta que exige TOTP sin
    /// haberlo superado nunca.
    /// </para>
    /// </summary>
    public static SegundoFactorMicrosoft DecidirSegundoFactor(bool mfaAcreditadaPorMicrosoft, bool totpConfigurado, bool vinculoNuevo) =>
        totpConfigurado && vinculoNuevo ? SegundoFactorMicrosoft.PedirTotp
        : mfaAcreditadaPorMicrosoft ? SegundoFactorMicrosoft.SesionDirecta
        : totpConfigurado ? SegundoFactorMicrosoft.PedirTotp
        : SegundoFactorMicrosoft.RechazarSinSegundoFactor;

    /// <summary>Clave de la identidad de Microsoft que se vinculará a la cuenta al superar el TOTP.</summary>
    public const string TipoClaimVinculoPendiente = "talveg:microsoft_vinculo_pendiente";

    /// <summary>Nombre visible del proveedor de ese vínculo pendiente.</summary>
    public const string TipoClaimVinculoPendienteNombre = "talveg:microsoft_vinculo_pendiente_nombre";

    /// <summary>
    /// El principal de la cookie de dos pasos de Identity
    /// (<see cref="IdentityConstants.TwoFactorUserIdScheme"/>), con la misma forma que
    /// escribe <c>PasswordSignInAsync</c>: el Id de la cuenta y el proveedor externo. Con
    /// él, <c>LoginCon2fa</c> verifica el TOTP como en el inicio con contraseña, e
    /// Identity deja el proveedor en la sesión que emite.
    /// <para>
    /// Con <paramref name="vinculoPendiente"/>, la identidad de Microsoft que se vinculará
    /// al superar el TOTP viaja en la misma cookie, cifrada y firmada por el servidor,
    /// junto al Id de la cuenta: no puede acabar vinculada a otra cuenta que la decidida
    /// aquí. El inicio con contraseña escribe su propia cookie de dos pasos, sin ella.
    /// </para>
    /// </summary>
    public static ClaimsPrincipal PrincipalSegundoFactorPendiente(Guid usuarioId, UserLoginInfo? vinculoPendiente)
    {
        var identidad = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identidad.AddClaim(new Claim(ClaimTypes.Name, usuarioId.ToString()));
        identidad.AddClaim(new Claim(ClaimTypes.AuthenticationMethod, AzureAdOptions.Esquema));
        if (vinculoPendiente is not null)
        {
            if (vinculoPendiente.LoginProvider != AzureAdOptions.Esquema)
                throw new ArgumentException("Solo se vinculan identidades de Microsoft.", nameof(vinculoPendiente));
            identidad.AddClaim(new Claim(TipoClaimVinculoPendiente, vinculoPendiente.ProviderKey));
            identidad.AddClaim(new Claim(TipoClaimVinculoPendienteNombre, vinculoPendiente.ProviderDisplayName ?? AzureAdOptions.Esquema));
        }
        return new ClaimsPrincipal(identidad);
    }

    /// <summary>
    /// El vínculo pendiente de la cookie de dos pasos que escribió el callback, o
    /// <c>null</c> si no hay (inicio con contraseña, o identidad ya vinculada).
    /// </summary>
    public static UserLoginInfo? VinculoPendiente(ClaimsPrincipal? principalSegundoFactor)
    {
        if (principalSegundoFactor?.FindFirstValue(ClaimTypes.AuthenticationMethod) != AzureAdOptions.Esquema)
            return null;
        var clave = principalSegundoFactor.FindFirstValue(TipoClaimVinculoPendiente);
        return string.IsNullOrWhiteSpace(clave)
            ? null
            : new UserLoginInfo(AzureAdOptions.Esquema, clave,
                principalSegundoFactor.FindFirstValue(TipoClaimVinculoPendienteNombre) ?? AzureAdOptions.Esquema);
    }

    /// <summary>
    /// Qué hacer con el vínculo pendiente al superar el TOTP, según los vínculos que
    /// la cuenta tenga ahora (pudieron cambiar desde el callback).
    /// </summary>
    public static DecisionVinculoTrasTotp DecidirVinculoTrasTotp(IEnumerable<UserLoginInfo> vinculosDeLaCuenta, string clave)
    {
        ArgumentNullException.ThrowIfNull(vinculosDeLaCuenta);
        var deMicrosoft = vinculosDeLaCuenta.Where(v => v.LoginProvider == AzureAdOptions.Esquema).ToList();
        if (deMicrosoft.Any(v => string.Equals(v.ProviderKey, clave, StringComparison.Ordinal)))
            return DecisionVinculoTrasTotp.YaVinculada;
        return deMicrosoft.Count > 0 ? DecisionVinculoTrasTotp.RechazarOtraIdentidad : DecisionVinculoTrasTotp.Vincular;
    }

    /// <summary>
    /// Vincula a la cuenta, tras superar su TOTP, la identidad de Microsoft que el
    /// callback dejó pendiente. <c>false</c> si no queda vinculada: la cuenta ya tiene
    /// otra identidad de Microsoft, la identidad ya es de otra cuenta, o el guardado falla. Quien llama cierra entonces la
    /// sesión recién emitida, como hace el callback cuando el vínculo no se guarda.
    /// </summary>
    public static async Task<bool> CompletarVinculoPendienteAsync(
        UserManager<ApplicationUser> userManager, ApplicationUser usuario, UserLoginInfo vinculo)
    {
        ArgumentNullException.ThrowIfNull(userManager);
        ArgumentNullException.ThrowIfNull(usuario);
        ArgumentNullException.ThrowIfNull(vinculo);

        switch (DecidirVinculoTrasTotp(await userManager.GetLoginsAsync(usuario), vinculo.ProviderKey))
        {
            case DecisionVinculoTrasTotp.YaVinculada:
                return true;
            case DecisionVinculoTrasTotp.RechazarOtraIdentidad:
                return false;
        }

        // La identidad pudo quedar vinculada a otra cuenta entre el callback y el TOTP
        // (la cookie de dos pasos vive minutos): no se vincula. Se mira antes de
        // guardar porque la clave del vínculo es única y el choque lanzaría.
        if (await userManager.FindByLoginAsync(vinculo.LoginProvider, vinculo.ProviderKey) is { } duena)
            return duena.Id == usuario.Id;

        if ((await userManager.AddLoginAsync(usuario, vinculo)).Succeeded)
            return true;

        // Dos verificaciones a la vez del mismo inicio: la otra pudo guardarlo antes.
        return DecidirVinculoTrasTotp(await userManager.GetLoginsAsync(usuario), vinculo.ProviderKey)
            == DecisionVinculoTrasTotp.YaVinculada;
    }

    // Un valor por notificación o, según la versión del lector de tokens, una lista JSON.
    private static IEnumerable<string> Valores(Claim claim)
    {
        var valor = claim.Value.Trim();
        if (!valor.StartsWith('['))
            return [valor];
        try
        {
            return JsonSerializer.Deserialize<string[]>(valor) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
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
        return (ComprobarCuentaActiva(decision, cuenta, DateTimeOffset.UtcNow), cuenta);
    }

    /// <summary>
    /// Una cuenta desactivada no entra ni se vincula por Microsoft.
    /// <c>SignInWithClaimsAsync</c> no comprueba el bloqueo (a diferencia de
    /// <c>PasswordSignInAsync</c>): sin esto, una cuenta desactivada volvía a entrar
    /// por Microsoft sin su contraseña (auditoría de seguridad 2026-09-20). Va antes
    /// de guardar el vínculo, para no dejar una identidad nueva colgada de una cuenta
    /// dada de baja.
    /// </summary>
    public static DecisionCuentaMicrosoft ComprobarCuentaActiva(DecisionCuentaMicrosoft decision, ApplicationUser? cuenta, DateTimeOffset ahora) =>
        decision is DecisionCuentaMicrosoft.EntrarConCuentaVinculada or DecisionCuentaMicrosoft.VincularCuentaExistente
        && cuenta is not null && cuenta.EstaDesactivada(ahora)
            ? DecisionCuentaMicrosoft.RechazarCuentaDesactivada
            : decision;

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
    ///
    /// <para>
    /// El inicio con Microsoft no crea cuentas (invitación previa, decisión D-3 de
    /// 2026-10-25). Solo entra quien ya tiene una cuenta de TALVEG en el Tenant
    /// destino con ese correo, creada antes por un Administrador o por el Actor de
    /// Plataforma TALVEG. Antes había alta automática sin rol: como el correo de
    /// Entra no está verificado, abría una cuenta a cualquier persona del directorio.
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

        return DecisionCuentaMicrosoft.RechazarSinCuentaPrevia;
    }
}

/// <summary>Resultado de <see cref="InicioSesionMicrosoft.DecidirCuenta"/>.</summary>
public enum DecisionCuentaMicrosoft
{
    /// <summary>La identidad ya estaba vinculada a una cuenta del Tenant destino.</summary>
    EntrarConCuentaVinculada,

    /// <summary>Cuenta del Tenant destino con el mismo correo y sin otra identidad de Microsoft: se vincula.</summary>
    VincularCuentaExistente,

    /// <summary>Ninguna cuenta con ese correo en el Tenant destino: no se crea nada; la tiene que dar de alta antes un Administrador.</summary>
    RechazarSinCuentaPrevia,

    /// <summary>La cuenta existe, pero está desactivada: ni se entra ni se vincula.</summary>
    RechazarCuentaDesactivada,

    /// <summary>La cuenta encontrada pertenece a otro Tenant propietario: nunca se entra en ella por Microsoft.</summary>
    RechazarCuentaDeOtroTenant,

    /// <summary>La cuenta del correo ya tiene otra identidad de Microsoft: un segundo vínculo sería una toma de cuenta.</summary>
    RechazarCuentaVinculadaAOtraIdentidad,

    /// <summary>Falta <see cref="AzureAdOptions.TenantHydraId"/>: no se sabe a qué Tenant pertenecería nadie.</summary>
    RechazarSinTenantDestino,
}

/// <summary>Resultado de <see cref="InicioSesionMicrosoft.DecidirSegundoFactor"/>.</summary>
public enum SegundoFactorMicrosoft
{
    /// <summary>Microsoft acreditó la MFA (<c>acrs</c> con el contexto configurado, o <c>amr</c> con <c>mfa</c>).</summary>
    SesionDirecta,

    /// <summary>Sin MFA de Microsoft: se pide el TOTP de TALVEG antes de emitir la sesión.</summary>
    PedirTotp,

    /// <summary>Sin MFA de Microsoft ni TOTP configurado: no se emite sesión.</summary>
    RechazarSinSegundoFactor,
}

/// <summary>Resultado de <see cref="InicioSesionMicrosoft.DecidirVinculoTrasTotp"/>.</summary>
public enum DecisionVinculoTrasTotp
{
    /// <summary>La cuenta no tiene identidad de Microsoft: se vincula la pendiente.</summary>
    Vincular,

    /// <summary>La cuenta ya tiene vinculada justo esta identidad: nada que hacer.</summary>
    YaVinculada,

    /// <summary>La cuenta ya tiene otra identidad de Microsoft: no se añade una segunda.</summary>
    RechazarOtraIdentidad,
}
