using CaeManager.Application.Common;
using MediatR;

namespace CaeManager.Application.Operaciones.IncorporacionCartera.Queries;

/// <summary>
/// Si quien mira participa en la solicitud de incorporación a cartera: es
/// Gestor CAE o Coordinador CAE <b>en su tenant de origen</b>. Es la puerta de
/// la interfaz (el enlace del menú lateral), que no puede decidirlo con
/// <c>IsInRole</c>: dentro de un Workspace operativo derivado el claim de rol
/// es el de la cartera en ese Tenant propietario, no el de su organización.
/// Mismo criterio que la bandeja (<see cref="ContextoOperadorCae"/>), para que
/// el enlace y lo que hay detrás no puedan divergir. No autoriza nada: cada
/// Query y cada Command vuelve a resolverlo.
/// </summary>
public record ParticipaEnIncorporacionCarteraQuery : IRequest<bool>;

public class ParticipaEnIncorporacionCarteraQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios)
    : IRequestHandler<ParticipaEnIncorporacionCarteraQuery, bool>
{
    public async Task<bool> Handle(ParticipaEnIncorporacionCarteraQuery request, CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        return contexto.EsExitoso && contexto.Valor.ParticipaEnIncorporacionCartera;
    }
}
