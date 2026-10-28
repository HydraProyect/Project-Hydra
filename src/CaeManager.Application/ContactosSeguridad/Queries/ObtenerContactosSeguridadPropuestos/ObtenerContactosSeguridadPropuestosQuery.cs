using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using MediatR;

namespace CaeManager.Application.ContactosSeguridad.Queries.ObtenerContactosSeguridadPropuestos;

/// <summary>
/// La cola de contactos de seguridad Propuestos de todos los Tenants beneficiarios, para que un Actor de
/// Plataforma TALVEG los valide o los rechace. Transversal por naturaleza: exige la concesión AdminPlataforma
/// global (una acotada no la satisface, por muchos Tenants que enumere) y falla cerrado con lista vacía,
/// como <c>ObtenerEstadoComercialTenantsQuery</c>. La RLS repite el criterio.
/// </summary>
public record ObtenerContactosSeguridadPropuestosQuery : IRequest<IReadOnlyList<ContactoSeguridadPropuestoDto>>;

public class ObtenerContactosSeguridadPropuestosQueryHandler(
    ICurrentUserService currentUserService,
    IAutorizacionAdminPlataforma autorizacion,
    IContactosSeguridadPlataforma plataforma)
    : IRequestHandler<ObtenerContactosSeguridadPropuestosQuery, IReadOnlyList<ContactoSeguridadPropuestoDto>>
{
    public async Task<IReadOnlyList<ContactoSeguridadPropuestoDto>> Handle(
        ObtenerContactosSeguridadPropuestosQuery request, CancellationToken cancellationToken)
    {
        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId
            || !await autorizacion.PuedeGlobalmenteAsync(usuarioId, cancellationToken))
            return [];

        var propuestos = await plataforma.ObtenerPropuestosAsync(cancellationToken);
        if (propuestos.Count == 0)
            return propuestos;

        // Una sola lectura para toda la cola. EsPropia compara con la misma cuenta autenticada (Actor real)
        // que usa LaPropusoQuienResuelveAsync al validar y rechazar, y solo decide qué botones se pintan:
        // quien resuelve sigue siendo la función de la base.
        var cuentas = await plataforma.ObtenerCuentasProponentesAsync(
            propuestos.Select(p => p.Id).ToList(), cancellationToken);
        return propuestos
            .Select(p => p with { EsPropia = cuentas.TryGetValue(p.Id, out var cuenta) && cuenta == usuarioId })
            .ToList();
    }
}
