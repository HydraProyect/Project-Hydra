using CaeManager.Infrastructure.Autenticacion;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace CaeManager.Web.Services;

/// <summary>
/// Evita la segunda autenticación de una petición de la extensión. El evaluador estándar vuelve a autenticar con los
/// esquemas de la política y reemplaza <c>HttpContext.User</c>; con <c>SesionOExtension</c> eso fusionaría además la
/// identidad de la cookie, y en cualquier política podría descartar la sustitución de rol que ya hizo
/// <see cref="RolEfectivoDelWorkspaceMiddleware"/>. Lo que resolvió <see cref="ContextoExtensionMiddleware"/> es
/// definitivo para esa petición, siempre que la política admita el esquema de extensión; el resto va por la base.
/// </summary>
public class EvaluadorPoliticaExtension(IAuthorizationService autorizacion) : PolicyEvaluator(autorizacion)
{
    public override Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
    {
        if (ContextoExtension.Es(context)
            && policy.AuthenticationSchemes.Contains(ExtensionAuthenticationSchemeOptions.NombreEsquema))
        {
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(context.User, ExtensionAuthenticationSchemeOptions.NombreEsquema)));
        }

        return base.AuthenticateAsync(policy, context);
    }
}
