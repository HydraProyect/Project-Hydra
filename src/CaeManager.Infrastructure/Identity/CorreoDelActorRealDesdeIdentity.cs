using CaeManager.Application.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Resuelve <see cref="ICorreoDelActorReal"/> contra Identity.
///
/// <para>
/// Toma el Id de <see cref="IActorAuditoria"/> y no de
/// <c>ICurrentUserService</c> a propósito: son los dos carriles de identidad
/// de ADR-011 § 8.5, y el correo al que debe volver la respuesta de una
/// reclamación firma autoría —"esto lo reclamo yo"—, que es exactamente lo
/// que el carril de auditoría garantiza irrenunciable. Cuando exista la
/// impersonación, el carril de autorización devolverá al usuario simulado y
/// este seguirá devolviendo a quien está detrás del teclado, sin que haya que
/// tocar nada aquí.
/// </para>
///
/// <para>
/// Lee <c>AspNetUsers</c> por Id, que es la propia cuenta del actor: no hay
/// filtro de tenant que aplicar ni que saltarse. Pasa por
/// <see cref="PuertaAccesoDatos"/> como el resto de accesos directos al
/// <c>UserManager</c> fuera de MediatR — es reentrante, así que la llamada
/// desde dentro de un handler no espera a nadie.
/// </para>
/// </summary>
public class CorreoDelActorRealDesdeIdentity(
    IActorAuditoria actorAuditoria,
    UserManager<ApplicationUser> userManager,
    PuertaAccesoDatos puertaAccesoDatos) : ICorreoDelActorReal
{
    public async Task<string?> ObtenerAsync(CancellationToken cancellationToken = default)
    {
        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.ActorRealUsuarioId is not { } usuarioId)
            return null;

        var correo = await puertaAccesoDatos.EjecutarAsync(async () =>
            await userManager.Users
                .Where(u => u.Id == usuarioId)
                .Select(u => u.Email)
                .FirstOrDefaultAsync(cancellationToken));

        return string.IsNullOrWhiteSpace(correo) ? null : correo;
    }
}
