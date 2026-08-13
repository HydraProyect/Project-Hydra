using CaeManager.Application.Common;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using CaeManager.Domain.ApiKeys;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Autenticacion;

public class ApiKeyAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
    public const string NombreEsquema = "ApiKey";
}

/// <summary>
/// Autentica peticiones a la API pública (P3-29) con el header
/// <c>Authorization: ApiKey {clave}</c>. Rellena los mismos claims que ya lee
/// el resto del sistema (<c>tenant_id</c> para <c>ITenantActual</c>,
/// <see cref="ClaimTypes.Role"/> para <c>IAlcanceDatosService</c>) — así el
/// aislamiento multi-tenant y la autorización existentes se aplican sin
/// tocar nada más (ver docs/MULTITENANCY.md § 8, "cuando exista API pública,
/// el tenant se resolverá del token").
///
/// Siempre <see cref="Roles.Consulta"/>: una clave de la API pública nunca
/// tiene más permiso que solo lectura en v1, sea cual sea el rol de quien la
/// generó.
/// </summary>
public class ApiKeyAuthenticationHandler(
    IOptionsMonitor<ApiKeyAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IClaveApiRepository claveRepositorio,
    IUnitOfWork unitOfWork)
    : AuthenticationHandler<ApiKeyAuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string EsquemaCabecera = "ApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var valorCabecera))
            return AuthenticateResult.NoResult();

        var cabecera = valorCabecera.ToString();
        if (!cabecera.StartsWith($"{EsquemaCabecera} ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var claveEnClaro = cabecera[(EsquemaCabecera.Length + 1)..].Trim();
        if (claveEnClaro.Length == 0)
            return AuthenticateResult.Fail("Falta la clave.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(claveEnClaro))).ToLowerInvariant();

        var clave = await claveRepositorio.ObtenerPorHashAsync(hash, Context.RequestAborted);
        if (clave is null || !clave.EstaActiva)
            return AuthenticateResult.Fail("Clave inválida o revocada.");

        await RegistrarUsoAsync(clave);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, clave.Id.ToString()),
            new Claim(ClaimTypes.Role, Roles.Consulta),
            new Claim(TenantClaimsPrincipalFactory.TipoClaimTenantId, clave.TenantId.ToString()),
        };

        var identidad = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identidad), Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Best-effort: si falla, la petición se autentica igual — perder el dato
    /// de "última vez que se usó" no debe tumbar la API pública.
    /// </summary>
    private async Task RegistrarUsoAsync(ClaveApi clave)
    {
        try
        {
            using var _ = AmbitoTenantExplicito.Establecer(clave.TenantId);
            clave.RegistrarUso();
            await unitOfWork.SaveChangesAsync(Context.RequestAborted);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "No se pudo registrar el último uso de la clave {ClaveApiId}.", clave.Id);
        }
    }
}
