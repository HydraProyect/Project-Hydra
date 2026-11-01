using System.Security.Claims;
using System.Text.Encodings.Web;
using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Autenticacion;

public class ClaveIntegracionAuthenticationSchemeOptions : AuthenticationSchemeOptions
{
    public const string NombreEsquema = "ClaveIntegracion";
    public const string TipoClaimAlcance = "alcance";
}

/// <summary>
/// Autentica <c>Authorization: Bearer {token}</c> con el token que da <see cref="EmisorTokenClaveIntegracion"/>.
/// El secreto de la clave no sirve aquí: solo se canjea en <c>POST /api/v1/token</c>.
///
/// <para>
/// El Tenant sale del token, que lo firmó TALVEG al canjear el secreto, y la fila se lee en ese Tenant bajo
/// RLS. Cada petición vuelve a comprobar todos los cortes, de la fila y del Tenant propietario, y carga los
/// alcances vigentes: nada de eso se guarda en el token.
/// </para>
///
/// <para>
/// Siempre <see cref="Roles.Consulta"/>, igual que la Clave de Soporte TALVEG: lectura. Los alcances
/// estrechan, nunca amplían. La baja de la cuenta emisora no corta (D-3b): la clave es de la organización.
/// </para>
/// </summary>
public class ClaveIntegracionAuthenticationHandler(
    IOptionsMonitor<ClaveIntegracionAuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IClaveIntegracionTenantRepository claves,
    IDataProtectionProvider dataProtectionProvider)
    : AuthenticationHandler<ClaveIntegracionAuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = CabeceraClaveIntegracion.ExtraerToken(Request.Headers.Authorization.ToString());
        if (token is null)
            return AuthenticateResult.NoResult();

        var carga = token.Length == 0 ? null : TokenClaveIntegracion.Leer(dataProtectionProvider, token);
        if (carga is null)
            return AuthenticateResult.Fail("Token inválido o caducado.");

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(carga.TenantId);

        var clave = await claves.ObtenerPorIdAsync(carga.ClaveId, Context.RequestAborted);
        if (clave is null || !clave.PuedeAutenticar(DateTime.UtcNow))
            return AuthenticateResult.Fail("Token inválido o caducado.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, clave.Id.ToString()),
            new(ClaimTypes.Role, Roles.Consulta),
            new(TenantClaimsPrincipalFactory.TipoClaimTenantId, carga.TenantId.ToString()),
        };
        claims.AddRange(clave.Alcances.Select(a => new Claim(ClaveIntegracionAuthenticationSchemeOptions.TipoClaimAlcance, a)));

        var identidad = new ClaimsIdentity(claims, Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identidad), Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }
}
