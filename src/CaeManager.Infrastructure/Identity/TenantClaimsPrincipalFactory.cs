using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Añade el claim <c>tenant_id</c> al construir el <see cref="ClaimsPrincipal"/>
/// de la sesión (ver docs/MULTITENANCY.md § 8, Tenant Resolution Strategy) —
/// se ejecuta en <c>SignInManager.SignInAsync</c> y en cada refresco
/// periódico del ticket de autenticación (mismo ciclo que ya refresca los
/// claims de rol). El valor sale directamente de <c>user.TenantId</c>, ya
/// cargado en memoria — no hace falta una consulta adicional a base de
/// datos, y no hay ningún riesgo de recursión con el filtro global de
/// <c>CaeManagerDbContext</c> (que no se aplica a <c>AspNetUsers</c>,
/// precisamente para que el login pueda resolver el tenant antes de
/// conocerlo).
/// </summary>
public class TenantClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> optionsAccessor)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole<Guid>>(userManager, roleManager, optionsAccessor)
{
    public const string TipoClaimTenantId = "tenant_id";

    public override async Task<ClaimsPrincipal> CreateAsync(ApplicationUser user)
    {
        var principal = await base.CreateAsync(user);

        if (principal.Identity is ClaimsIdentity identidad)
            identidad.AddClaim(new Claim(TipoClaimTenantId, user.TenantId.ToString()));

        return principal;
    }
}
