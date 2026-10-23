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

/// <remarks>
/// Con <see cref="MemoParticipaEnIncorporacionCartera"/> y <see cref="ITenantActual"/> (la composición real los
/// registra) se resuelve una vez por ámbito y contexto (<see cref="ClaveContextoMemo"/>) dentro de su caducidad: el
/// menú lateral y el selector de Tenant beneficiario la piden en el prerenderizado y otra vez en el circuito. Sin
/// ellos (tests que construyen el handler a mano) se resuelve en cada llamada, como antes.
/// </remarks>
public class ParticipaEnIncorporacionCarteraQueryHandler(
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService directorioUsuarios,
    ITenantActual? tenantActual = null, MemoParticipaEnIncorporacionCartera? memo = null)
    : IRequestHandler<ParticipaEnIncorporacionCarteraQuery, bool>
{
    public async Task<bool> Handle(ParticipaEnIncorporacionCarteraQuery request, CancellationToken cancellationToken)
    {
        if (memo is not null && tenantActual is not null
            && await ClaveContextoMemo.ResolverAsync(currentUserService, tenantActual) is { } clave)
            return await memo.ObtenerOCalcularAsync(clave, CalcularAsync, cancellationToken);

        return await CalcularAsync(cancellationToken);
    }

    private async Task<bool> CalcularAsync(CancellationToken cancellationToken)
    {
        var contexto = await ContextoOperadorCae.ResolverAsync(currentUserService, directorioUsuarios, cancellationToken);
        return contexto.EsExitoso && contexto.Valor.ParticipaEnIncorporacionCartera;
    }
}

/// <summary>Memoización Scoped de <see cref="ParticipaEnIncorporacionCarteraQuery"/> (<see cref="MemoPorContexto{TValor}"/>).</summary>
public sealed class MemoParticipaEnIncorporacionCartera(TimeProvider? reloj = null) : MemoPorContexto<bool>(reloj);
