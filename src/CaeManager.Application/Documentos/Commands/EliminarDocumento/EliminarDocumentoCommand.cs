using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.Documentos.Commands.EliminarDocumento;

public record EliminarDocumentoCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarDocumentoCommandHandler(IDocumentoRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarDocumentoCommand, Result>
{
    public async Task<Result> Handle(EliminarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (documento is null)
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento."));

        documento.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
