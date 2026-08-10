using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Subcontratas;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.EliminarVerificacionExterna;

/// <summary>
/// Soft delete de una verificación registrada por error. La consulta de
/// supervisión deriva el estado de la última verificación <b>no eliminada</b>
/// (ADR-005 § 2.3), así que eliminar la última "rehabilita" la anterior — el
/// historial real nunca se pierde. La evidencia adjunta no se borra del
/// almacenamiento: soft delete significa que la fila puede restaurarse.
/// </summary>
public record EliminarVerificacionExternaSubcontrataCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarVerificacionExternaSubcontrataCommandHandler(
    IVerificacionExternaSubcontrataRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarVerificacionExternaSubcontrataCommand, Result>
{
    public async Task<Result> Handle(
        EliminarVerificacionExternaSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var verificacion = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (verificacion is null || !await alcanceDatos.SubcontrataVisibleAsync(verificacion.SubcontrataId, cancellationToken))
            return Result.Fallo(Error.Crear("VerificacionExterna.NoEncontrada", "No encontramos esta verificación."));

        verificacion.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
