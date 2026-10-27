using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Queries.ResolverDestinatariosAvisoSeguridad;

/// <summary>
/// A quién avisar, por Tenant beneficiario afectado, de una violación de seguridad (RGPD art. 33): el
/// contacto de seguridad validado o, si el Tenant no tiene, sus Administradores con la marca
/// <see cref="DestinatariosAvisoSeguridadTenant.SinContactoValidado"/> (decisión del propietario del
/// 2026-10-26). Solo resuelve: no envía nada (el envío y la página de confirmación son otro incremento).
///
/// <para>
/// Lo pide un Actor de Plataforma TALVEG con concesión AdminPlataforma global, el mismo que confirma el
/// aviso. Sin ella, fallo y no lista vacía: una lista vacía se leería como «nadie a quien avisar».
/// </para>
/// </summary>
public record ResolverDestinatariosAvisoSeguridadQuery(IReadOnlyCollection<Guid> TenantIds)
    : IRequest<Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>>;

public class ResolverDestinatariosAvisoSeguridadQueryHandler(
    ICurrentUserService currentUserService,
    IAutorizacionAdminPlataforma autorizacion,
    IContactosSeguridadPlataforma plataforma)
    : IRequestHandler<ResolverDestinatariosAvisoSeguridadQuery, Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>>
{
    public async Task<Result<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>> Handle(
        ResolverDestinatariosAvisoSeguridadQuery request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || !await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken))
            return Result.Fallo<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>(ErroresContactoSeguridad.SoloActorDePlataforma);

        var tenantIds = request.TenantIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (tenantIds.Count == 0)
            return Result.Exito<IReadOnlyList<DestinatariosAvisoSeguridadTenant>>([]);

        return await plataforma.ResolverDestinatariosAsync(tenantIds, cancellationToken);
    }
}
