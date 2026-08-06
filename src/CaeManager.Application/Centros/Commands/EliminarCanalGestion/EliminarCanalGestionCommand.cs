using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EliminarCanalGestion;

/// <summary>
/// Baja de un acceso de gestión documental del Centro (PLAN-EJECUCION-UX.md
/// § 0.6, Lote 0-E). Soft delete, como el resto de agregados: son credenciales
/// de acceso a un portal de cliente, borrarlas de verdad por un clic no tiene
/// vuelta atrás.
///
/// Borrar el principal teniendo otros se rechaza en vez de promover uno solo:
/// cuál de los N pasaría a ser el acceso por defecto del Centro es una
/// decisión del gestor, no algo que adivinar por orden de inserción.
/// </summary>
public record EliminarCanalGestionCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarCanalGestionCommandHandler(
    ICanalGestionDocumentalRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarCanalGestionCommand, Result>
{
    public async Task<Result> Handle(EliminarCanalGestionCommand request, CancellationToken cancellationToken)
    {
        var canal = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (canal is null || !await alcanceDatos.CentroVisibleAsync(canal.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("CanalGestion.NoEncontrado", "No encontramos este acceso."));

        if (canal.EsPrincipal)
        {
            var hermanos = await repositorio.ObtenerPorCentroAsync(canal.CentroId, cancellationToken);
            if (hermanos.Count > 1)
                return Result.Fallo(Error.Crear(
                    "CanalGestion.PrincipalConHermanos",
                    "Este es el acceso principal del centro. Marca antes otro como principal y vuelve a intentarlo."));
        }

        canal.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
