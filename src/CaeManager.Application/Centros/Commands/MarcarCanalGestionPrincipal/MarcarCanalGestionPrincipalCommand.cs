using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Centros.Commands.MarcarCanalGestionPrincipal;

/// <summary>
/// Designa el canal por defecto del Centro (PLAN-EJECUCION-UX.md § 0.6, Lote
/// 0-E). Desmarca el anterior en la misma transacción — "a lo sumo uno" lo
/// sostiene además un índice único filtrado, ver
/// <c>CanalGestionDocumentalConfiguration</c>.
/// </summary>
public record MarcarCanalGestionPrincipalCommand(Guid Id) : ICommand;

public class MarcarCanalGestionPrincipalCommandHandler(
    ICanalGestionDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarCanalGestionPrincipalCommand, Result>
{
    public async Task<Result> Handle(MarcarCanalGestionPrincipalCommand request, CancellationToken cancellationToken)
    {
        var canal = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (canal is null || !await alcanceDatos.CentroVisibleAsync(canal.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("CanalGestion.NoEncontrado", "No encontramos este acceso."));

        if (canal.EsPrincipal)
            return Result.Exito();

        foreach (var hermano in await repositorio.ObtenerPorCentroAsync(canal.CentroId, cancellationToken))
        {
            if (hermano.EsPrincipal)
                hermano.DejarDeSerPrincipal();
        }

        canal.MarcarComoPrincipal();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
