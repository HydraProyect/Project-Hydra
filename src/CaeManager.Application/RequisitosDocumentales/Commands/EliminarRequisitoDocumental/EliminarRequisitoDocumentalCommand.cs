using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.RequisitosDocumentales.Commands.EliminarRequisitoDocumental;

public record EliminarRequisitoDocumentalCommand(Guid Id) : ICommand;

public class EliminarRequisitoDocumentalCommandHandler(
    IRequisitoDocumentalRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarRequisitoDocumentalCommand, Result>
{
    public async Task<Result> Handle(EliminarRequisitoDocumentalCommand request, CancellationToken cancellationToken)
    {
        var requisito = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (requisito is null || !await alcanceDatos.CentroVisibleAsync(requisito.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("RequisitoDocumental.NoEncontrado", "El requisito no existe o no tienes acceso."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        requisito.MarcarComoEliminado(usuarioId ?? Guid.Empty);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
