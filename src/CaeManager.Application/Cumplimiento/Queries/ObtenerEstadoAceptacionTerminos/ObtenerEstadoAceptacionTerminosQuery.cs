using CaeManager.Application.Common;
using CaeManager.Domain.Cumplimiento;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoAceptacionTerminos;

/// <summary>True si el usuario autenticado actual todavía no aceptó la versión vigente de los Términos y Condiciones + Política de Privacidad.</summary>
public record ObtenerEstadoAceptacionTerminosQuery : IRequest<bool>;

public class ObtenerEstadoAceptacionTerminosQueryHandler(
    IAceptacionTerminosRepository repositorio, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerEstadoAceptacionTerminosQuery, bool>
{
    public async Task<bool> Handle(ObtenerEstadoAceptacionTerminosQuery request, CancellationToken cancellationToken)
    {
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null) return false;

        return !await repositorio.ExisteParaVersionAsync(usuarioId.Value, VersionTerminos.Actual, cancellationToken);
    }
}
