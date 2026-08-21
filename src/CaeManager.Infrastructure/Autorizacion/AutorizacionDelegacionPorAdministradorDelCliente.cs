using CaeManager.Application.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Autorizacion;

/// <inheritdoc cref="IAutorizacionDelegacionTenant" />
/// <remarks>
/// <para>
/// Dos condiciones, y las dos hacen falta: el usuario tiene el rol
/// <c>Administrador</c> <b>y</b> pertenece al tenant del Cliente Delegante.
/// Cualquiera de las dos por separado autoriza a la persona equivocada — un
/// Administrador de la Consultora tiene el rol pero es la parte que recibe el
/// acceso, y un usuario cualquiera del Cliente Delegante está en el tenant
/// correcto sin autoridad para conceder nada.
/// </para>
///
/// <para>
/// <b>Ambas se resuelven contra la base, no contra la sesión</b>, y es
/// deliberado. <c>ICurrentUserService.ObtenerRolActualAsync</c> devuelve el rol
/// <i>efectivo en el contexto actual</i>: dentro de un workspace delegado es el
/// de la cartera, no el que la persona tiene en su propia organización. Un
/// Administrador del Cliente Delegante que estuviera operando otro workspace
/// vería su propio rol sustituido, y esta autorización le diría que no por un
/// motivo que no tiene nada que ver con su autoridad. La pertenencia
/// (<c>ApplicationUser.TenantId</c>) y el rol de Identity no cambian con el
/// workspace activo.
/// </para>
///
/// <para>
/// Efecto colateral correcto: un usuario de plataforma queda fuera sin
/// necesidad de comprobarlo. Su <c>TenantId</c> es el de plataforma, que nunca
/// será el del Cliente Delegante, así que la primera condición ya falla — y eso
/// es justo lo que ADR-004 § 11.1 exige, que Hydra no pueda iniciar una
/// delegación.
/// </para>
/// </remarks>
public class AutorizacionDelegacionPorAdministradorDelCliente(
    UserManager<ApplicationUser> userManager) : IAutorizacionDelegacionTenant
{
    public async Task<bool> PuedeGestionarDelegacionesAsync(
        Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default)
    {
        if (tenantClienteDeleganteId == Guid.Empty) return false;

        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
        if (usuario is null) return false;

        // El tenant primero: es la mitad que distingue a quien concede de quien
        // recibe, y evita consultar roles de un usuario que ya sabemos que no
        // pinta nada aquí.
        if (usuario.TenantId != tenantClienteDeleganteId) return false;

        return await userManager.IsInRoleAsync(usuario, Roles.Administrador);
    }
}
