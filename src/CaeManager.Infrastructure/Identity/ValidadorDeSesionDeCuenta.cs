using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// El <c>SecurityStampValidator</c> de la cookie, con el bloqueo añadido: el del
/// framework solo compara el stamp y una cuenta bloqueada con el stamp intacto
/// pasaba, y encima recibía otros 14 días de cookie (auditoría 2026-09-20).
///
/// <para>
/// Se sustituye la <b>implementación registrada de
/// <see cref="ISecurityStampValidator"/></b> (ver <c>AddInfrastructure</c>), no
/// el delegado <c>OnValidatePrincipal</c>: el framework solo llama a
/// <see cref="VerifySecurityStamp"/> cuando vence
/// <see cref="SesionDeCuenta.IntervaloDeValidacion"/>, así que el bloqueo se
/// comprueba con la misma cadencia que el stamp y no en cada petición. Cuando
/// esto devuelve <c>null</c> el validador base rechaza el principal y borra la
/// cookie. <c>OmitirRevalidacionDeStampEnRuta</c> (Program.cs) envuelve el
/// delegado que llama a este validador y sigue funcionando igual.
/// </para>
/// </summary>
public sealed class ValidadorDeSesionDeCuenta(
    IOptions<SecurityStampValidatorOptions> opciones,
    SignInManager<ApplicationUser> signInManager,
    ILoggerFactory loggerFactory)
    : SecurityStampValidator<ApplicationUser>(opciones, signInManager, loggerFactory)
{
    protected override async Task<ApplicationUser?> VerifySecurityStamp(ClaimsPrincipal? principal)
    {
        var usuario = await base.VerifySecurityStamp(principal);
        if (usuario is null) return null;

        return await SignInManager.UserManager.IsLockedOutAsync(usuario) ? null : usuario;
    }
}
